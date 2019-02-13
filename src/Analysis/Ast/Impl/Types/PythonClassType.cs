﻿// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Analysis.Modules;
using Microsoft.Python.Analysis.Specializations.Typing;
using Microsoft.Python.Analysis.Types.Collections;
using Microsoft.Python.Analysis.Utilities;
using Microsoft.Python.Analysis.Values;
using Microsoft.Python.Analysis.Values.Collections;
using Microsoft.Python.Core;
using Microsoft.Python.Parsing.Ast;

namespace Microsoft.Python.Analysis.Types {
    [DebuggerDisplay("Class {Name}")]
    internal class PythonClassType : PythonType, IPythonClassType, IPythonTemplateType, IEquatable<IPythonClassType> {
        private readonly object _lock = new object();
        private readonly AsyncLocal<IPythonClassType> _processing = new AsyncLocal<IPythonClassType>();
        private IReadOnlyList<IPythonType> _mro;

        // For tests
        internal PythonClassType(string name, IPythonModule declaringModule, LocationInfo location = null)
            : base(name, declaringModule, string.Empty, location ?? LocationInfo.Empty, BuiltinTypeId.Type) { }

        public PythonClassType(
            ClassDefinition classDefinition,
            IPythonModule declaringModule,
            LocationInfo location,
            BuiltinTypeId builtinTypeId = BuiltinTypeId.Type
        ) : base(classDefinition.Name, declaringModule, classDefinition.GetDocumentation(), location, builtinTypeId) {
            ClassDefinition = classDefinition;
        }

        #region IPythonType
        public override PythonMemberType MemberType => PythonMemberType.Class;

        public override IEnumerable<string> GetMemberNames() {
            var names = new HashSet<string>();
            lock (_lock) {
                names.UnionWith(Members.Keys);
            }
            foreach (var m in Mro.Skip(1)) {
                names.UnionWith(m.GetMemberNames());
            }
            return names;
        }

        public override IMember GetMember(string name) {
            IMember member;
            lock (_lock) {
                if (Members.TryGetValue(name, out member)) {
                    return member;
                }

                // Special case names that we want to add to our own Members dict
                switch (name) {
                    case "__mro__":
                        member = AddMember(name, PythonCollectionType.CreateList(DeclaringModule.Interpreter, LocationInfo.Empty, Mro), true);
                        return member;
                }
            }
            if (Push(this)) {
                try {
                    foreach (var m in Mro.Reverse()) {
                        if (m == this) {
                            return member;
                        }
                        member = member ?? m.GetMember(name);
                    }
                } finally {
                    Pop();
                }
            }
            return null;
        }

        public override string Documentation {
            get {
                // Try doc from the type (class definition AST node).
                var doc = base.Documentation;
                // Try bases.
                if (string.IsNullOrEmpty(doc) && Bases != null) {
                    doc = Bases.FirstOrDefault(b => !string.IsNullOrEmpty(b?.Documentation))?.Documentation;
                }
                // Try docs __init__.
                if (string.IsNullOrEmpty(doc)) {
                    doc = GetMember("__init__")?.GetPythonType()?.Documentation;
                }
                return doc;
            }
        }

        // Constructor call
        public override IMember CreateInstance(string typeName, LocationInfo location, IArgumentSet args) {
            // Specializations
            switch (typeName) {
                case "list":
                    return PythonCollectionType.CreateList(DeclaringModule.Interpreter, location, args);
                case "dict": {
                        // self, then contents
                        var contents = args.Values<IMember>().Skip(1).FirstOrDefault();
                        return new PythonDictionary(DeclaringModule.Interpreter, location, contents);
                    }
                case "tuple": {
                        var contents = args.Values<IMember>();
                        return PythonCollectionType.CreateTuple(DeclaringModule.Interpreter, location, contents);
                    }
            }
            return new PythonInstance(this, location);
        }
        #endregion

        #region IPythonClass
        public ClassDefinition ClassDefinition { get; }
        public IReadOnlyList<IPythonType> Bases { get; private set; }

        public IReadOnlyList<IPythonType> Mro {
            get {
                lock (_lock) {
                    if (_mro != null) {
                        return _mro;
                    }
                    if (Bases == null) {
                        //Debug.Fail("Accessing Mro before SetBases has been called");
                        return new IPythonType[] { this };
                    }
                    _mro = new IPythonType[] { this };
                    _mro = CalculateMro(this);
                    return _mro;
                }
            }
        }
        #endregion

        internal void SetBases(IEnumerable<IPythonType> bases) {
            lock (_lock) {
                if (Bases != null) {
                    return; // Already set
                }

                Bases = bases.MaybeEnumerate().ToArray();
                if (Bases.Count > 0) {
                    AddMember("__base__", Bases[0], true);
                }

                if (!(DeclaringModule is BuiltinsPythonModule)) {
                    // TODO: If necessary, we can set __bases__ on builtins when the module is fully analyzed.
                    AddMember("__bases__", PythonCollectionType.CreateList(DeclaringModule.Interpreter, LocationInfo.Empty, Bases), true);
                }
            }
        }

        internal static IReadOnlyList<IPythonType> CalculateMro(IPythonType cls, HashSet<IPythonType> recursionProtection = null) {
            if (cls == null) {
                return Array.Empty<IPythonType>();
            }
            if (recursionProtection == null) {
                recursionProtection = new HashSet<IPythonType>();
            }
            if (!recursionProtection.Add(cls)) {
                return Array.Empty<IPythonType>();
            }
            try {
                var mergeList = new List<List<IPythonType>> { new List<IPythonType>() };
                var finalMro = new List<IPythonType> { cls };

                var bases = (cls as PythonClassType)?.Bases;
                if (bases == null) {
                    var members = (cls.GetMember("__bases__") as IPythonCollection)?.Contents ?? Array.Empty<IMember>();
                    bases = members.Select(m => m.GetPythonType()).ToArray();
                }

                foreach (var b in bases) {
                    var b_mro = new List<IPythonType>();
                    b_mro.AddRange(CalculateMro(b, recursionProtection));
                    mergeList.Add(b_mro);
                }

                while (mergeList.Any()) {
                    // Next candidate is the first head that does not appear in
                    // any other tails.
                    var nextInMro = mergeList.FirstOrDefault(mro => {
                        var m = mro.FirstOrDefault();
                        return m != null && !mergeList.Any(m2 => m2.Skip(1).Contains(m));
                    })?.FirstOrDefault();

                    if (nextInMro == null) {
                        // MRO is invalid, so return just this class
                        return new[] { cls };
                    }

                    finalMro.Add(nextInMro);

                    // Remove all instances of that class from potentially being returned again
                    foreach (var mro in mergeList) {
                        mro.RemoveAll(ns => ns == nextInMro);
                    }

                    // Remove all lists that are now empty.
                    mergeList.RemoveAll(mro => !mro.Any());
                }

                return finalMro;
            } finally {
                recursionProtection.Remove(cls);
            }
        }

        private bool Push(IPythonClassType cls) {
            if (_processing.Value == null) {
                _processing.Value = cls;
                return true;
            }
            return false;
        }

        private void Pop() => _processing.Value = null;
        public bool Equals(IPythonClassType other)
            => Name == other?.Name && DeclaringModule.Equals(other?.DeclaringModule);

        public async Task<IPythonType> CreateSpecificTypeAsync(IArgumentSet args, IPythonModule declaringModule, LocationInfo location, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();

            var genericBases = Bases.Where(b => b is IGenericType).ToArray();
            // TODO: handle optional generics as class A(Generic[_T1], Optional[Generic[_T2]])
            if (genericBases.Length != args.Arguments.Count) {
                // TODO: report parameters mismatch.
            }

            // Create concrete type
            var bases = args.Arguments.Select(a => a.Value).OfType<IPythonType>().ToArray();
            var specificName = CodeFormatter.FormatSequence(Name, '[', bases);
            var classType = new PythonClassType(specificName, declaringModule);

            // Prevent reentrancy when resolving generic class where
            // method may be returning instance of type of the same class.
            if (!Push(classType)) {
                return _processing.Value;
            }

            try {
                // Optimistically use what is available even if there is an argument mismatch.
                // TODO: report unresolved types?
                classType.SetBases(bases);

                // Add members from the template class (this one).
                classType.AddMembers(this, true);

                // Resolve return types of methods, if any were annotated as generics
                var members = classType.GetMemberNames()
                    .Except(new[] { "__class__", "__bases__", "__base__" })
                    .ToDictionary(n => n, n => classType.GetMember(n));

                foreach (var m in members) {
                    switch (m.Value) {
                        case IPythonFunctionType fn: {
                                foreach (var o in fn.Overloads.OfType<PythonFunctionOverload>()) {
                                    var returnType = o.StaticReturnValue.GetPythonType();
                                    if (returnType is PythonClassType cls && cls.IsGeneric()) {
                                        // -> A[_E]
                                        if (!cls.Equals(classType)) {
                                            // Prevent reentrancy
                                            var specificReturnValue = await cls.CreateSpecificTypeAsync(args, declaringModule, location, cancellationToken);
                                            o.SetReturnValue(new PythonInstance(specificReturnValue, location), true);
                                        }
                                    } else if (returnType is IGenericTypeParameter) {
                                        // -> _T
                                        var b = bases.FirstOrDefault();
                                        if (b != null) {
                                            o.SetReturnValue(new PythonInstance(b, location), true);
                                        }
                                    }
                                }
                                break;
                            }
                        case IPythonTemplateType tt: {
                                var specificType = await tt.CreateSpecificTypeAsync(args, declaringModule, location, cancellationToken);
                                classType.AddMember(m.Key, specificType, true);
                                break;
                            }
                        case IPythonInstance inst: {
                                if (inst.GetPythonType() is IPythonTemplateType tt && tt.IsGeneric()) {
                                    var specificType = await tt.CreateSpecificTypeAsync(args, declaringModule, location, cancellationToken);
                                    classType.AddMember(m.Key, new PythonInstance(specificType, location), true);
                                }
                                break;
                            }
                    }
                }
            } finally {
                Pop();
            }
            return classType;
        }
    }
}
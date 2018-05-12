﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Build
{
    public class TypeBuilder
    {
        private readonly ITypeFilter _typeFilter;
        private readonly ITypeParser _typeParser;
        private readonly ITypeResolver _typeResolver;
        private IDictionary<string, RuntimeType> _types = new Dictionary<string, RuntimeType>();

        private HashSet<Type> visited = new HashSet<Type>();

        public TypeBuilder()
        {
            _typeFilter = new TypeFilter();
            _typeResolver = new TypeResolver();
            _typeParser = new TypeParser();
        }

        public TypeBuilder(ITypeFilter typeFilter, ITypeResolver typeResolver, ITypeParser typeParser)
        {
            _typeFilter = typeFilter ?? throw new ArgumentNullException(nameof(typeFilter));
            _typeResolver = typeResolver ?? throw new ArgumentNullException(nameof(typeResolver));
            _typeParser = typeParser ?? throw new ArgumentNullException(nameof(typeParser));
        }

        public ITypeFilter Filter => _typeFilter;

        public ITypeParser Parser => _typeParser;

        public IEnumerable<string> RegisteredIds => _types.Keys;

        public ITypeResolver Resolver => _typeResolver;

        internal IEnumerable<RuntimeType> RegisteredTypes => _types.Values;

        private RuntimeType this[string typeId, RuntimeType type]
        {
            get
            {
                if (!_types.ContainsKey(typeId))
                    _types.Add(typeId, type);
                _types[typeId].RegisterType(type.Type);
                return _types[typeId];
            }
        }

        public bool CanCreate(Type type) => _typeFilter.CanCreate(type);

        public bool CanRegister(Type type) => _typeFilter.CanRegister(type);

        public object CreateInstance(Type type, params object[] args)
        {
            return CreateInstance(type.FullName, args);
        }

        public object CreateInstance(string typeId, params object[] args)
        {
            if (_types.ContainsKey(typeId))
                return _types[typeId].CreateInstance(args);
            string[] parameterArgs = args == null ? new string[0] : args.Select(p => p == null ? typeof(object).FullName : p.GetType().FullName).ToArray();
            var runtimeType = (RuntimeType)_typeParser.Find(typeId, parameterArgs, _types.Values);
            string typeFullName = _typeResolver.GetTypeFullName(runtimeType, parameterArgs, typeId);
            if (runtimeType != null) return runtimeType.CreateInstance(args);
            throw new TypeInstantiationException(string.Format("{0} is not instantiated (no constructors available)", typeId));
        }

        public void RegisterType(Type type) => RegisterTypeId(type);

        private void RegisterConstructor(Type type, ConstructorInfo constructor)
        {
            var constructorTypeFullName = string.Format("{0}({1})", type.FullName, string.Join(",", constructor.GetParameters().Select(p => p.ParameterType.FullName)));
            var runtimeType = new RuntimeType(constructor.GetCustomAttribute<DependencyAttribute>() ?? new DependencyAttribute(type, RuntimeInstance.CreateInstance), null, type);
            var parameters = constructor.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                var attribute = parameters[i].GetCustomAttribute<InjectionAttribute>() ?? new InjectionAttribute(parameters[i].ParameterType/*, new object[parameters.Length]*/);
                var parameterType = parameters[i].ParameterType;
                string typeId = _typeResolver.GetTypeId(attribute, parameterType.FullName);
                var attributeType = _typeResolver.GetType(type.Assembly, typeId);
                if (attributeType != null && !parameterType.IsAssignableFrom(attributeType))
                    throw new TypeRegistrationException(string.Format("{0} is not registered (not assignable from {1})", parameterType.FullName, attributeType.FullName));
                if (typeId == type.FullName && typeId == parameterType.FullName)
                    throw new TypeRegistrationException(string.Format("{0} is not registered (circular references found)", type.FullName));
                string[] parameterArgs = attribute.Args.Select((p) => p == null ? typeof(object).FullName : p.GetType().FullName).ToArray();
                if (attributeType != null && _typeFilter.CanRegister(attributeType) && _typeParser.Find(typeId, parameterArgs, _types.Values) == null)
                {
                    if (visited.Contains(attributeType))
                        throw new TypeRegistrationException(string.Format("{0} is not registered (circular references found)", parameterType.FullName));
                    try
                    {
                        RegisterTypeId(attributeType);
                    }
                    catch (TypeRegistrationException ex)
                    {
                        throw ex;
                    }
                }
                var injectedType = (RuntimeType)_typeParser.Find(typeId, parameterArgs, _types.Values);
                string typeFullName = _typeResolver.GetTypeFullName(injectedType, parameterArgs, typeId);
                if (_typeFilter.CanRegister(parameterType))
                {
                    if (visited.Contains(parameterType))
                        throw new TypeRegistrationException(string.Format("{0} is not registered (circular references found)", parameterType.FullName));
                    RegisterTypeId(parameterType);
                }
                var parameterRuntimeTypeBase = new RuntimeType(attribute, runtimeType, parameterType, attribute.Args);
                var parameterRuntimeType = this[typeFullName, parameterRuntimeTypeBase];
                parameterRuntimeType.Attribute.RegisterRuntimeType(string.Format("{0}:({1})", constructorTypeFullName, i), attribute);
                runtimeType.AddParameter(parameterRuntimeType);
            }
            RegisterConstructorType(constructor, type, runtimeType);
        }

        private void RegisterConstructorType(ConstructorInfo constructor, Type type, RuntimeType baseRuntimeType)
        {
            var constructorTypeFullName = string.Format("{0}({1})", type.FullName, string.Join(",", constructor.GetParameters().Select(p => p.ParameterType.FullName)));
            var attribute = baseRuntimeType.Attribute;
            string typeId = _typeResolver.GetTypeId(attribute, type.FullName);
            var attributeType = _typeResolver.GetType(type.Assembly, typeId);
            if (attributeType != null && !attributeType.IsAssignableFrom(type))
                throw new TypeRegistrationException(string.Format("{0} is not registered (not assignable from {1})", attributeType.FullName, type.FullName));
            var parameterArgs = constructor.GetParameters().Select(p => p.ParameterType.FullName).ToArray();
            var injectedType = (RuntimeType)_typeParser.Find(typeId, parameterArgs, _types.Values);
            string typeFullName = _typeResolver.GetTypeFullName(injectedType, parameterArgs, typeId);
            if (_types.ContainsKey(typeFullName) && this[typeFullName, _types[typeFullName]].IsInitialized)
                return;
            var runtimeType = this[typeFullName, baseRuntimeType];
            if (runtimeType != null)
            {
                runtimeType.Initialize(attribute.Runtime, type);
            }
        }

        private void RegisterTypeId(Type type)
        {
            string typeId = type.FullName;
            var constructors = type.GetConstructors();
            if (constructors.Length == 0)
                throw new TypeRegistrationException(string.Format("{0} is not registered (no constructors available)", type.FullName));
            visited.Add(type);
            foreach (var constructor in constructors)
            {
                RegisterConstructor(type, constructor);
            }
            visited.Remove(type);
        }
    }
}
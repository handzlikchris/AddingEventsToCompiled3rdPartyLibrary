﻿using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace EventILWeaver.Weaver
{
    public class IlEventGenerator
    {
        public const string IlWeavedAutoGeneratedEventAttributeName = "IlWeavedAutoGeneratedEvent";
        private readonly AssemblyDefinition _assembly;
        private readonly CustomAttribute _markCreatedEventsWithAttribute;
        private readonly ModuleDefinition _module;
        private readonly CustomAttribute _compilerGeneratedAttribute;
        private readonly TypeReference _delegateType;
        private readonly MethodReference _delegateCombineMethod;
        private readonly TypeReference _interlockedType;
        private readonly MethodReference _interlockedCompareExchangeMethod;
        private readonly MethodReference _delegateRemoveMethod;

        public IlEventGenerator(AssemblyDefinition assembly)
        {
            _assembly = assembly;
            _module = _assembly.MainModule;
            _compilerGeneratedAttribute = GetCompilerGeneratedAttibute(_module);
            _markCreatedEventsWithAttribute = CreateOrGetAutoGenerateEventAttribute(_module);

            _delegateType = _module.ImportReference(typeof(Delegate));
            _delegateCombineMethod = _module.ImportReference(_delegateType.Resolve().Methods
                .First(m => m.Name == nameof(Delegate.Combine) && m.Parameters.Count == 2));
            _delegateRemoveMethod = _module.ImportReference(_delegateType.Resolve().Methods
                .First(m => m.Name == nameof(Delegate.Remove) && m.Parameters.Count == 2));

            _interlockedType = _module.ImportReference(typeof(Interlocked));
            _interlockedCompareExchangeMethod = _module.ImportReference(_interlockedType.Resolve().Methods.First(m =>
                m.Name == nameof(Interlocked.CompareExchange) && m.GenericParameters.Count == 1 &&
                m.Parameters.Count == 3));
        }

        public GenerateEventResult GenerateEvent(TypeReference eventHandlerGenericParamType, string eventName)
        {
            var handlerType = _module.ImportReference(typeof(EventHandler<>));
            var handlerGenericParamType = _module.ImportReference(eventHandlerGenericParamType.Resolve());

            var genericHandlerType = new GenericInstanceType(handlerType);
            genericHandlerType.GenericArguments.Add(handlerGenericParamType);
            var genericHandlerTypeResolved = _module.ImportReference(genericHandlerType);

            var eventField = new FieldDefinition(eventName, FieldAttributes.Private, genericHandlerTypeResolved);
            eventField.CustomAttributes.Add(_compilerGeneratedAttribute);

            var addMethod = GenerateAddHandlerMethod(eventName, genericHandlerTypeResolved, eventField);
            var removeMethod = GenerateEventRemoveMethod(eventName, genericHandlerTypeResolved, eventField);
            var ev = new EventDefinition(eventName, Mono.Cecil.EventAttributes.None, genericHandlerTypeResolved)
            {
                AddMethod = addMethod, 
                RemoveMethod = removeMethod,
            };
            ev.CustomAttributes.Add(_markCreatedEventsWithAttribute);
            
            return new GenerateEventResult(ev, eventField, genericHandlerTypeResolved, handlerGenericParamType);
        }


        private MethodDefinition GenerateEventRemoveMethod(string eventName, TypeReference genericHandlerTypeResolved,
            FieldDefinition eventField)
        {
            var removeMethod = new MethodDefinition($"remove_{eventName}",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName, 
                _module.TypeSystem.Void);
            var removeMethodParameter = new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.None, genericHandlerTypeResolved);
            removeMethod.Parameters.Add(removeMethodParameter);
            removeMethod.CustomAttributes.Add(_compilerGeneratedAttribute);

            removeMethod.Body.Variables.Add(new VariableDefinition(genericHandlerTypeResolved));
            removeMethod.Body.Variables.Add(new VariableDefinition(genericHandlerTypeResolved));
            removeMethod.Body.Variables.Add(new VariableDefinition(genericHandlerTypeResolved));

            var il = removeMethod.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, eventField));
            il.Append(il.Create(OpCodes.Stloc_0));

            var loopStart = il.Create(OpCodes.Ldloc_0);
            il.Append(loopStart);
            il.Append(il.Create(OpCodes.Stloc_1));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Ldarg_1));

            il.Append(il.Create(OpCodes.Call, _delegateRemoveMethod));
            il.Append(il.Create(OpCodes.Castclass, genericHandlerTypeResolved));

            il.Append(il.Create(OpCodes.Stloc_2));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldflda, eventField));
            il.Append(il.Create(OpCodes.Ldloc_2));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Call, GenerateGenericInterlockedCompareMethod(genericHandlerTypeResolved)));
            il.Append(il.Create(OpCodes.Stloc_0));
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Bne_Un_S, loopStart));

            il.Append(il.Create(OpCodes.Ret));
            return removeMethod;
        }

        private MethodDefinition GenerateAddHandlerMethod(string eventName, TypeReference genericHandlerTypeResolved, FieldDefinition eventField)
        {
            var addMethod = new MethodDefinition($"add_{eventName}",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName, 
                _module.TypeSystem.Void);
            var addMethodParameter = new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.None, genericHandlerTypeResolved);
            addMethod.Parameters.Add(addMethodParameter);
            addMethod.CustomAttributes.Add(_compilerGeneratedAttribute);

            addMethod.Body.Variables.Add(new VariableDefinition(genericHandlerTypeResolved));
            addMethod.Body.Variables.Add(new VariableDefinition(genericHandlerTypeResolved));
            addMethod.Body.Variables.Add(new VariableDefinition(genericHandlerTypeResolved));

            var il = addMethod.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, eventField));
            il.Append(il.Create(OpCodes.Stloc_0));

            var loopStart = il.Create(OpCodes.Ldloc_0);
            il.Append(loopStart);
            il.Append(il.Create(OpCodes.Stloc_1));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Ldarg_1));


            il.Append(il.Create(OpCodes.Call, _delegateCombineMethod));
            il.Append(il.Create(OpCodes.Castclass, genericHandlerTypeResolved));

            il.Append(il.Create(OpCodes.Stloc_2));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldflda, eventField));
            il.Append(il.Create(OpCodes.Ldloc_2));
            il.Append(il.Create(OpCodes.Ldloc_1));

            il.Append(il.Create(OpCodes.Call, GenerateGenericInterlockedCompareMethod(genericHandlerTypeResolved)));

            il.Append(il.Create(OpCodes.Stloc_0));
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Bne_Un_S, loopStart));

            il.Append(il.Create(OpCodes.Ret));
            return addMethod;
        }

        private GenericInstanceMethod GenerateGenericInterlockedCompareMethod(TypeReference genericHandlerTypeResolved)
        {
            var genericCompareExchangeMethod = new GenericInstanceMethod(_interlockedCompareExchangeMethod);
            genericCompareExchangeMethod.GenericArguments.Add(genericHandlerTypeResolved);

            return genericCompareExchangeMethod;
        }


        private static CustomAttribute GetCompilerGeneratedAttibute(ModuleDefinition module)
        {
            var attrConstructor = module.ImportReference(module.ImportReference(typeof(CompilerGeneratedAttribute)).Resolve().Methods
                .First(m => m.IsConstructor));
            return new CustomAttribute(attrConstructor);
        }

        private static CustomAttribute CreateOrGetAutoGenerateEventAttribute(ModuleDefinition module)
        {
            var autoGeneratedAttributeType = module.Types.SingleOrDefault(t => t.Name == IlWeavedAutoGeneratedEventAttributeName);
            if (autoGeneratedAttributeType == null)
            {
                var attributeDefinition = new TypeDefinition(
                    "",
                    IlWeavedAutoGeneratedEventAttributeName,
                    Mono.Cecil.TypeAttributes.NestedPrivate);
                var attributeType = module.ImportReference(typeof(Attribute));
                attributeDefinition.BaseType = attributeType;

                AddEmptyConstructor(module, attributeDefinition, module.ImportReference(attributeType.Resolve().GetConstructors().First()));

                module.Types.Add(attributeDefinition);

                autoGeneratedAttributeType = module.Types.Single(t => t.Name == IlWeavedAutoGeneratedEventAttributeName);
            }
            
            var attrConstructor = module.ImportReference(module.ImportReference(autoGeneratedAttributeType).Resolve().Methods
                .First(m => m.IsConstructor));
            return new CustomAttribute(attrConstructor);
        }

        private static void AddEmptyConstructor(ModuleDefinition module, TypeDefinition type, MethodReference baseEmptyConstructor)
        {
            var methodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            var method = new MethodDefinition(".ctor", methodAttributes, module.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, baseEmptyConstructor));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
        }

        private class CreateEventFieldResult
        {
            public FieldDefinition FieldDefinition { get; }
            public TypeReference GenericHandlerTypeResolved { get; }

            public CreateEventFieldResult(FieldDefinition fieldDefinition, TypeReference genericHandlerTypeResolved)
            {
                FieldDefinition = fieldDefinition;
                GenericHandlerTypeResolved = genericHandlerTypeResolved;
            }
        }

    }
}
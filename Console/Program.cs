﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Scriban;
using Scriban.Runtime;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;

namespace Console
{
    public class HookPropertySetResult
    {
        public bool IsSuccess => String.IsNullOrEmpty(ErrorMessage);
        public string ErrorMessage { get; }

        public HookPropertySetResult(string errorMessage)
        {
            ErrorMessage = errorMessage;
        }
    }

    public class IlEventHookManager
    {
        private readonly AssemblyDefinition _assembly;
        private readonly ModuleDefinition _module;

        public IlEventHookManager(AssemblyDefinition assembly)
        {
            _assembly = assembly;
            _module = _assembly.MainModule;
        }


        public HookPropertySetResult HookPropertySet(GenerateEventResult generateEventResult, string addToType, string propertyName)
        {
            var addEventToType = _module.Types.Single(t => t.Name == addToType);
            if (addEventToType.Events.Any(ev => ev.Name == generateEventResult.EventDefinition.Name))
            {
                return new HookPropertySetResult($"'{generateEventResult.EventDefinition.FullName}' already existing in type '{addToType}', skipping...");
            }

            addEventToType.Fields.Add(generateEventResult.FieldDefinition);
            addEventToType.Methods.Add(generateEventResult.EventDefinition.AddMethod);
            addEventToType.Methods.Add(generateEventResult.EventDefinition.RemoveMethod);
            addEventToType.Events.Add(generateEventResult.EventDefinition);
            System.Console.WriteLine($"Added event: {generateEventResult.EventDefinition.Name} to '{addEventToType.Name}'");

            InjectEventCallAtPropertySetterStart(propertyName, addEventToType, generateEventResult);
            System.Console.WriteLine($"Event: {generateEventResult.EventDefinition.Name} will be called on start property-setter '{addEventToType.Name}:{propertyName}'");

            return new HookPropertySetResult(null);
        }

        private void InjectEventCallAtPropertySetterStart(string propertyName, TypeDefinition addEventToType, GenerateEventResult generateEventResult)
        {
            //change existing method to call into event
            var setMethod = addEventToType.Methods.Single(m => m.Name == $"set_{propertyName}");
            var il = setMethod.Body.GetILProcessor();
            var firstInstruction = setMethod.Body.Instructions.First();

            var loadThisArgForEventCall = il.Create(OpCodes.Ldarg_0);
            il.InsertBefore(firstInstruction, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(firstInstruction, il.Create(OpCodes.Ldfld, generateEventResult.FieldDefinition));
            il.InsertBefore(firstInstruction, il.Create(OpCodes.Dup));
            il.InsertBefore(firstInstruction, il.Create(OpCodes.Brtrue, loadThisArgForEventCall));
            il.InsertBefore(firstInstruction, il.Create(OpCodes.Pop));
            il.InsertBefore(firstInstruction, il.Create(OpCodes.Br, firstInstruction));

            il.InsertBefore(firstInstruction, loadThisArgForEventCall);
            il.InsertBefore(firstInstruction, il.Create(OpCodes.Ldarg_1));

            var genericInvoke = CreateGenericInvokeMethodReference(generateEventResult.GenericHandlerParamType, generateEventResult.FieldDefinition);
            il.InsertBefore(firstInstruction, il.Create(OpCodes.Callvirt, genericInvoke));
        }

        private MethodReference CreateGenericInvokeMethodReference(TypeReference eventHandlerGenericParamType, FieldDefinition eventField)
        {
            var invokeMethod = _module.ImportReference(eventField.FieldType.Resolve().Methods
                .Single(m => m.Name == nameof(EventHandler.Invoke)));

            var genericInvoke = MakeHostInstanceGeneric(invokeMethod, eventHandlerGenericParamType);
            return genericInvoke;
        }

        private static MethodReference MakeHostInstanceGeneric(MethodReference self, params TypeReference[] arguments)
        {
            var reference = new MethodReference(self.Name, self.ReturnType, self.DeclaringType.MakeGenericInstanceType(arguments))
            {
                HasThis = self.HasThis,
                ExplicitThis = self.ExplicitThis,
                CallingConvention = self.CallingConvention
            };

            foreach (var parameter in self.Parameters)
                reference.Parameters.Add(new
                    ParameterDefinition(parameter.ParameterType));

            foreach (var generic_parameter in self.GenericParameters)
                reference.GenericParameters.Add(new
                    GenericParameter(generic_parameter.Name, reference));

            return reference;
        }

    }

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

    public class GenerateEventResult
    {
        public EventDefinition EventDefinition { get; }
        public FieldDefinition FieldDefinition { get; }
        public TypeReference GenericHandlerTypeResolved { get; }
        public TypeReference GenericHandlerParamType { get; }

        public GenerateEventResult(EventDefinition eventDefinition, FieldDefinition fieldDefinition, TypeReference genericHandlerTypeResolved, TypeReference genericHandlerParamType)
        {
            FieldDefinition = fieldDefinition;
            GenericHandlerTypeResolved = genericHandlerTypeResolved;
            GenericHandlerParamType = genericHandlerParamType;
            EventDefinition = eventDefinition;
        }
    }

    [Verb("add-events", HelpText = "Weave IL instructions to library")]
    class AddEventsOptions
    {
        public const char MultipleDelimiter = ';';

        private const string TargetDefinitionHelpText = "Weaving target definitions in form: ObjectTypeName-PropertyName-PropertyTypeName, " +
                                                        "delimited with ';' for multiple values, eg. 'Transform-position;Transform-rotation'";
        public const string TargetDllPathHelpText = "Location of DLL that will be weaved, multiple paths delimited with ;'";

        private IEnumerable<string> _targetDefinitionsRaw;

        [Option('t',"target-dll-paths", Separator = MultipleDelimiter, Required = true, HelpText = TargetDllPathHelpText)]
        public IEnumerable<string> TargetDllPaths { get; set; }

        [Option("target-definitions", Required = true, Separator = MultipleDelimiter, HelpText = TargetDefinitionHelpText)]
        public IEnumerable<string> TargetDefinitionsRaw
        {
            get => _targetDefinitionsRaw;
            set
            {
                _targetDefinitionsRaw = value;
                if (_targetDefinitionsRaw != null && TargetDefinitions == null)
                {
                    ParseTargetDefinitions();
                }
            }
        }

        public List<TargetDefinition> TargetDefinitions { get; set; }

        private void ParseTargetDefinitions()
        {
            TargetDefinitions = TargetDefinitionsRaw.Select(r =>
            {
                var splitted = r.Split(new[] {"-"}, StringSplitOptions.RemoveEmptyEntries);
                if (splitted.Length != 2)
                    throw new Exception($"Unable to parse {nameof(TargetDefinitionsRaw)}, make sure values are in correct format.\r\n{TargetDefinitionHelpText}");

                return new TargetDefinition(splitted[0], splitted[1]);
            }).ToList();
        }
    }

    [Verb("revert-to-original", HelpText = "Revert to original library.")]
    class RevertToOriginalOptions
    {
        [Option('t', "target-dll-paths", Separator = AddEventsOptions.MultipleDelimiter, Required = true, HelpText = AddEventsOptions.TargetDllPathHelpText)]
        public IEnumerable<string> TargetDllPaths { get; set; }

    }

    [Verb("list-existing", HelpText = "Shows existing auto generated events")]
    class ListExistingAutoGeneratedEventsOptions
    {
        [Option('t', "target-dll-path", Required = true, HelpText = AddEventsOptions.TargetDllPathHelpText)]
        public string TargetDllPath { get; set; }

    }

    [Verb("generate-helper-code", HelpText = "Generates helper code for auto-generated events that abstracts direct event access, " +
                                             "in case when DLL is not weaved with events it'll not fail to build but fallback to specific code instead, eg. logging.")]
    class GenerateHelperCodeOptions
    {
        [Option('t', "target-dll-path", Required = true, HelpText = AddEventsOptions.TargetDllPathHelpText)]
        public string TargetDllPath { get; set; }

        [Option('o', "output-file", Required = true, HelpText = "Output file where generated code will be saved")]
        public string OutputFile { get; set; }

        [Option('n', "namespace", Required = true, HelpText = "Namespace to be used for generated code")]
        public string Namespace { get; set; }

        [Option("enabled-build-symbol", Required = true, HelpText = "Helper code will be conditionally compiled, based on that symbol. This is a fallback mechanism in case DLL is not weaved but code should still build")]
        public string EnabledBuildSymbol { get; set; }

        [Option("using-statements", Required = false, Separator = ':', HelpText = "Using statements to be included on top of the file, delimited with :")]
        public IEnumerable<string> UsingStatements { get; set; }

        [Option("include-custom-code-when-no-build-symbol", Required = false, HelpText = "This code will be injected to methods that'll be executed if build symbol is not specified " +
                                                                                         "(which could mean library is not weaved for whatever reason). You could add any code, eg. some logging")]
        public string IncludeCustomCodeWhenNoBuildSymbol { get; set; }

    }

    public class TargetDefinition
    {
        public string ObjectTypeName { get; set; }
        public string PropertyName { get; set; }

        public TargetDefinition(string objectTypeName, string propertyName)
        {
            ObjectTypeName = objectTypeName;
            PropertyName = propertyName;
        }
    }

    public class TypeWithAutoGeneratedEvents
    {
        public TypeDefinition Type { get; }
        public IEnumerable<EventDefinition> EventsWithAutoGeneratedAttribute { get; }

        public TypeWithAutoGeneratedEvents(TypeDefinition type, IEnumerable<EventDefinition> eventsWithAutoGeneratedAttribute)
        {
            Type = type;
            EventsWithAutoGeneratedAttribute = eventsWithAutoGeneratedAttribute;
        }
    }

    public class Program
    {
        private static IlEventGenerator _ilEventGenerator;
        private static IlEventHookManager _ilEventManager;

        public static string GenerateDefaultSetPropertyEventName(string propertyName) => $"Set{char.ToUpper(propertyName[0]) + propertyName.Substring(1)}Executing";

        static int Main(string[] args)
        {
            return Parser.Default.ParseArguments<AddEventsOptions, RevertToOriginalOptions, ListExistingAutoGeneratedEventsOptions, GenerateHelperCodeOptions>(args)
                .MapResult(
                    (AddEventsOptions opts) => RunAddEvents(opts),
                    (RevertToOriginalOptions opts) => RunRevertToOriginal(opts),
                    (ListExistingAutoGeneratedEventsOptions opts) => RunListExistingAutoGeneratedEvents(opts),
                    (GenerateHelperCodeOptions opts) => RunGenerateHelperCode(opts),
                    errs => 1);
        }

        private static int RunAddEvents(AddEventsOptions options)
        {
            foreach (var targetPath in options.TargetDllPaths)
            {
                System.Console.WriteLine($"Processing... {targetPath}");
                
                if (!CreateCleanCopyFromBackup(targetPath))
                {
                    System.Console.WriteLine($"Unable to {nameof(CreateCleanCopyFromBackup)}, exiting...");
                    return 1;
                }

                using (var assembly = AssemblyDefinition.ReadAssembly(targetPath, new ReaderParameters { ReadWrite = true }))
                {
                    _ilEventGenerator = new IlEventGenerator(assembly);
                    _ilEventManager = new IlEventHookManager(assembly);

                    foreach (var targetDefinition in options.TargetDefinitions)
                    {
                        var propertyType = ResolveSetterValueArgType(targetDefinition.ObjectTypeName, targetDefinition.PropertyName, assembly.MainModule);
                        CreateEventAndWeaveCallAtSetterStart(targetDefinition.ObjectTypeName, targetDefinition.PropertyName, propertyType);
                    }

                    assembly.Write();
                }

                System.Console.WriteLine($"Processed! {targetPath}\r\n\r\n");
            }

            EndWhenUserReady();
            return 0;
        }

        private static void EndWhenUserReady()
        {
            System.Console.WriteLine("\r\n\r\nPress any key to exit...");
            System.Console.ReadKey();
        }

        private static int RunRevertToOriginal(RevertToOriginalOptions options)
        {
            foreach (var targetPath in options.TargetDllPaths)
            {
                System.Console.WriteLine($"Processing... {targetPath}");
                
                RevertToBackup(targetPath);
                
                System.Console.WriteLine($"Processed! {targetPath}\r\n\r\n");
            }

            EndWhenUserReady();
            return 0;
        }

        private static int RunListExistingAutoGeneratedEvents(ListExistingAutoGeneratedEventsOptions options)
        {
            var existingTypesWithAutoGeneratedEvents = GetExistingTypesWithAutoGeneratedEvents(options.TargetDllPath);

            var sb = new StringBuilder($"Existing events which have {IlEventGenerator.IlWeavedAutoGeneratedEventAttributeName} attribute:\r\n\r\n");
                foreach (var existingTypeWithAutoGeneratedEvents in existingTypesWithAutoGeneratedEvents)
                {
                    sb.AppendLine(existingTypeWithAutoGeneratedEvents.Type.Name);
                    foreach (var ev in existingTypeWithAutoGeneratedEvents.EventsWithAutoGeneratedAttribute)
                    {
                        sb.AppendLine("\t" + ev.FullName);
                    }
                }

                System.Console.WriteLine(sb.ToString());


            EndWhenUserReady();
            return 0;
        }


        private static readonly string HelperCodeTemplate =
@"//THIS IS AUTO GENERATED CODE, ANY CHANGES WILL BE OVERWRITTEN
{{ for using in Model.Usings -}}
using {{using}};
{{end}}
namespace {{Model.Namespace}} 
{
#if {{Model.EnabledBuildSymbol}}
	{{ for typeWithEvents in Model.TypeWithEvents -}}
	public static class {{typeWithEvents.Type.Name}}Extensions 
	{
		{{ for event in typeWithEvents.EventsWithAutoGeneratedAttribute }}
		public static void Bind{{event.Name}}(this {{typeWithEvents.Type.Name}} obj, EventHandler<{{event.EventType.GenericArguments[0].Name}}> handler)
	    {
	        obj.{{event.Name}} += handler;
	    }
	
		public static void UnBind{{event.Name}}(this {{typeWithEvents.Type.Name}} obj, EventHandler<{{event.EventType.GenericArguments[0].Name}}> handler)
	    {
	        obj.{{event.Name}} -= handler;
	    }
		{{end}}
	}

	{{end}}
#else
	{{ for typeWithEvents in Model.TypeWithEvents -}}
	public static class {{typeWithEvents.Type.Name}}Extensions 
	{
		{{ for event in typeWithEvents.EventsWithAutoGeneratedAttribute }}
		public static void Bind{{event.Name}}(this {{typeWithEvents.Type.Name}} obj, EventHandler<{{event.EventType.GenericArguments[0].Name}}> handler)
	    {
			{{if Model.IncludeCustomCodeWhenNoBuildSymbol -}}
			{{Model.IncludeCustomCodeWhenNoBuildSymbol}}
			{{else}}
			//No implementation on purpose
			{{-end}}
	    }
	
		public static void UnBind{{event.Name}}(this {{typeWithEvents.Type.Name}} obj, EventHandler<{{event.EventType.GenericArguments[0].Name}}> handler)
	    {
			{{if Model.IncludeCustomCodeWhenNoBuildSymbol -}}
			{{Model.IncludeCustomCodeWhenNoBuildSymbol}}
			{{else}}
			//No implementation on purpose
			{{-end}}
	    }
		{{end}}
	}
	{{-end}}
#endif
}";

        private static int RunGenerateHelperCode(GenerateHelperCodeOptions options)
        {
            var existingTypesWithAutoGeneratedEvents = GetExistingTypesWithAutoGeneratedEvents(options.TargetDllPath);

            var template = Template.Parse(HelperCodeTemplate);
            var scriptObject = new ScriptObject
            {
                ["Model"] = new Dictionary<string, object>
                {
                    ["Namespace"] = options.Namespace,
                    ["Usings"] = options.UsingStatements,
                    ["IncludeCustomCodeWhenNoBuildSymbol"] = options.IncludeCustomCodeWhenNoBuildSymbol,
                    ["EnabledBuildSymbol"] = options.EnabledBuildSymbol,
                    ["TypeWithEvents"] = existingTypesWithAutoGeneratedEvents
                }
            };

            var context = new TemplateContext() { MemberRenamer = member => member.Name };
            context.PushGlobal(scriptObject);
            var helperCode = template.Render(context);

            File.WriteAllText(options.OutputFile, helperCode);

            System.Console.WriteLine($"Helper code generated and saved at '{options.OutputFile}'");

            EndWhenUserReady();
            return 0;
        }

        public static List<TypeWithAutoGeneratedEvents> GetExistingTypesWithAutoGeneratedEvents(string targetDllPath)
        {
            List<TypeWithAutoGeneratedEvents> existingTypesWithAutoGeneratedEvents;
            using (var assembly = AssemblyDefinition.ReadAssembly(targetDllPath, new ReaderParameters {ReadWrite = false}))
            {
                existingTypesWithAutoGeneratedEvents = assembly.MainModule.Types.Select(t =>
                    {
                        return new TypeWithAutoGeneratedEvents(t,
                            t.Events.Where(e => e.CustomAttributes.Any(ca =>
                                ca.AttributeType.Name == IlEventGenerator.IlWeavedAutoGeneratedEventAttributeName)));
                    })
                    .Where(t => t.EventsWithAutoGeneratedAttribute.Any())
                    .ToList();
            }

            return existingTypesWithAutoGeneratedEvents;
        }

        private static void CreateEventAndWeaveCallAtSetterStart(string typeName, string propName, TypeReference propType)
        {
            var eventName = GenerateDefaultSetPropertyEventName(propName);
            var generatedEvent = _ilEventGenerator.GenerateEvent(propType, eventName);
            var result = _ilEventManager.HookPropertySet(generatedEvent, typeName, propName);
            if(!result.IsSuccess)
                System.Console.WriteLine(result.ErrorMessage);
        }

        private static TypeReference ResolveSetterValueArgType(string typeName, string propertyName, ModuleDefinition module)
        {
            var type = module.Types.Single(t => t.Name == typeName);
            var setMethod = type.Methods.Single(m => m.Name == $"set_{propertyName}");

            return setMethod.Parameters.First().ParameterType;
        }

        private static bool RevertToBackup(string dllPath)
        {
            return ExecuteWithOptionalRetry(() =>
            {
                var backupPath = CreateBackupFilePath(dllPath);
                if (!File.Exists(backupPath))
                {
                    System.Console.WriteLine("Backup does not exist, unable to revert!");
                    return;
                }

                if (File.Exists(dllPath)) File.Delete(dllPath);

                File.Move(backupPath, dllPath);
                System.Console.WriteLine("Backup restored");
            });
        }


        private static bool CreateCleanCopyFromBackup(string dllPath)
        {
            return ExecuteWithOptionalRetry(() =>
            {
                var backupPath = CreateBackupFilePath(dllPath);
                if (!File.Exists(backupPath))
                {
                    System.Console.WriteLine("Backup does not exist, creating");
                    File.Copy(dllPath, backupPath);
                    System.Console.WriteLine($"Backup created: '{backupPath}'");
                }

                if (File.Exists(dllPath)) File.Delete(dllPath);

                File.Copy(backupPath, dllPath);
            });
        }

        private static bool ExecuteWithOptionalRetry(Action execute)
        {
            bool retry;
            do
            {
                try
                {
                    execute();
                    retry = false;
                }
                catch (UnauthorizedAccessException e)
                {
                    System.Console.WriteLine(
                        "Unable to modify dll, make sure you run the application as administrator and close all applications that use library.");

                    System.Console.Write("\r\nRetry? [y]es, any other key for no\t: ");
                    var key = System.Console.ReadKey().Key;
                    System.Console.WriteLine();
                    if (key == ConsoleKey.Y)
                    {
                        retry = true;
                    }
                    else
                    {
                        return false;
                    }
                }
            } while (retry);

            return true;
        }

        private static string CreateBackupFilePath(string dllPath)
        {
            return $@"{dllPath}.backup";
        }
    }
}

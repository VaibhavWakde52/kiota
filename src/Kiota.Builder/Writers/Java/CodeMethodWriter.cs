﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Kiota.Builder.CodeDOM;
using Kiota.Builder.Extensions;
using Kiota.Builder.OrderComparers;

namespace Kiota.Builder.Writers.Java;
public class CodeMethodWriter : BaseElementWriter<CodeMethod, JavaConventionService>
{
    public CodeMethodWriter(JavaConventionService conventionService) : base(conventionService) { }
    public override void WriteCodeElement(CodeMethod codeElement, LanguageWriter writer)
    {
        ArgumentNullException.ThrowIfNull(codeElement);
        if (codeElement.ReturnType == null) throw new InvalidOperationException($"{nameof(codeElement.ReturnType)} should not be null");
        ArgumentNullException.ThrowIfNull(writer);
        if (codeElement.Parent is not CodeClass parentClass) throw new InvalidOperationException("the parent of a method should be a class");

        var baseReturnType = conventions.GetTypeString(codeElement.ReturnType, codeElement);
        var finalReturnType = GetFinalReturnType(codeElement, baseReturnType);
        WriteMethodDocumentation(codeElement, writer, baseReturnType, finalReturnType);
        WriteMethodPrototype(codeElement, writer, finalReturnType);
        writer.IncreaseIndent();
        var inherits = parentClass.StartBlock.Inherits != null && !parentClass.IsErrorDefinition;
        var requestBodyParam = codeElement.Parameters.OfKind(CodeParameterKind.RequestBody);
        var configParam = codeElement.Parameters.OfKind(CodeParameterKind.RequestConfiguration);
        var requestContentType = codeElement.Parameters.OfKind(CodeParameterKind.RequestBodyContentType);
        var requestParams = new RequestParams(requestBodyParam, configParam, requestContentType);
        AddNullChecks(codeElement, writer);
        switch (codeElement.Kind)
        {
            case CodeMethodKind.Serializer:
                WriteSerializerBody(parentClass, codeElement, writer, inherits);
                break;
            case CodeMethodKind.Deserializer:
                WriteDeserializerBody(codeElement, parentClass, writer, inherits);
                break;
            case CodeMethodKind.IndexerBackwardCompatibility:
                WriteIndexerBody(codeElement, parentClass, writer, finalReturnType);
                break;
            case CodeMethodKind.RequestGenerator when codeElement.IsOverload:
                WriteGeneratorOrExecutorMethodCall(codeElement, requestParams, parentClass, writer, "return ", CodeMethodKind.RequestGenerator);
                break;
            case CodeMethodKind.RequestGenerator when !codeElement.IsOverload:
                WriteRequestGeneratorBody(codeElement, requestParams, parentClass, writer);
                break;
            case CodeMethodKind.RequestExecutor when codeElement.IsOverload:
                WriteGeneratorOrExecutorMethodCall(codeElement, requestParams, parentClass, writer, "return ", CodeMethodKind.RequestExecutor);
                break;
            case CodeMethodKind.RequestExecutor when !codeElement.IsOverload:
                WriteRequestExecutorBody(codeElement, requestParams, parentClass, writer);
                break;
            case CodeMethodKind.Getter:
                WriteGetterBody(codeElement, writer, parentClass);
                break;
            case CodeMethodKind.Setter:
                WriteSetterBody(codeElement, writer, parentClass);
                break;
            case CodeMethodKind.RawUrlBuilder:
                WriteRawUrlBuilderBody(parentClass, codeElement, writer);
                break;
            case CodeMethodKind.ClientConstructor:
                WriteConstructorBody(parentClass, codeElement, writer, inherits);
                WriteApiConstructorBody(parentClass, codeElement, writer);
                break;
            case CodeMethodKind.Constructor when codeElement.IsOverload && parentClass.IsOfKind(CodeClassKind.RequestBuilder):
                WriteRequestBuilderConstructorCall(codeElement, writer);
                break;
            case CodeMethodKind.Constructor:
            case CodeMethodKind.RawUrlConstructor:
                WriteConstructorBody(parentClass, codeElement, writer, inherits);
                break;
            case CodeMethodKind.RequestBuilderWithParameters:
                WriteRequestBuilderBody(parentClass, codeElement, writer);
                break;
            case CodeMethodKind.RequestBuilderBackwardCompatibility:
                throw new InvalidOperationException("RequestBuilderBackwardCompatibility is not supported as the request builders are implemented by properties.");
            case CodeMethodKind.Factory when !codeElement.IsOverload:
                WriteFactoryMethodBody(codeElement, parentClass, writer);
                break;
            case CodeMethodKind.Factory when codeElement.IsOverload:
                WriteFactoryOverloadMethod(codeElement, parentClass, writer);
                break;
            case CodeMethodKind.ErrorMessageOverride:
                WriteErrorMethodOverride(parentClass, writer);
                break;
            case CodeMethodKind.ComposedTypeMarker:
                throw new InvalidOperationException("ComposedTypeMarker is not required as interface is explicitly implemented.");
            default:
                writer.WriteLine("return null;");
                break;
        }
        writer.CloseBlock();
    }
    private static void WriteErrorMethodOverride(CodeClass parentClass, LanguageWriter writer)
    {
        if (parentClass.IsErrorDefinition && parentClass.GetPrimaryMessageCodePath(static x => x.Name.ToFirstCharacterLowerCase(), static x => x.Name.ToFirstCharacterLowerCase() + "()") is string primaryMessageCodePath && !string.IsNullOrEmpty(primaryMessageCodePath))
        {
            writer.WriteLine($"return this.{primaryMessageCodePath};");
        }
        else
        {
            writer.WriteLine("return super.getMessage();");
        }
    }
    private void WriteRawUrlBuilderBody(CodeClass parentClass, CodeMethod codeElement, LanguageWriter writer)
    {
        var rawUrlParameter = codeElement.Parameters.OfKind(CodeParameterKind.RawUrl) ?? throw new InvalidOperationException("RawUrlBuilder method should have a RawUrl parameter");
        var requestAdapterProperty = parentClass.GetPropertyOfKind(CodePropertyKind.RequestAdapter) ?? throw new InvalidOperationException("RawUrlBuilder method should have a RequestAdapter property");
        writer.WriteLine($"return new {parentClass.Name}({rawUrlParameter.Name}, {requestAdapterProperty.Name});");
    }
    private const string ResultVarName = "result";
    private void WriteFactoryMethodBody(CodeMethod codeElement, CodeClass parentClass, LanguageWriter writer)
    {
        var parseNodeParameter = codeElement.Parameters.OfKind(CodeParameterKind.ParseNode) ?? throw new InvalidOperationException("Factory method should have a ParseNode parameter");
        if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForUnionType || parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForIntersectionType)
            writer.WriteLine($"final {parentClass.Name} {ResultVarName} = new {parentClass.Name}();");
        var writeDiscriminatorValueRead = parentClass.DiscriminatorInformation.ShouldWriteParseNodeCheck && !parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForIntersectionType;
        if (writeDiscriminatorValueRead)
        {
            writer.WriteLine($"final ParseNode mappingValueNode = {parseNodeParameter.Name}.getChildNode(\"{parentClass.DiscriminatorInformation.DiscriminatorPropertyName}\");");
            writer.StartBlock("if (mappingValueNode != null) {");
            writer.WriteLine($"final String {DiscriminatorMappingVarName} = mappingValueNode.getStringValue();");
        }
        if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForInheritedType)
            if (parentClass.DiscriminatorInformation.DiscriminatorMappings.Count() > MaxDiscriminatorsPerMethod)
                WriteSplitFactoryMethodBodyForInheritedModel(parentClass, writer);
            else
                WriteFactoryMethodBodyForInheritedModel(parentClass.DiscriminatorInformation.DiscriminatorMappings, writer);
        else if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForUnionType && parentClass.DiscriminatorInformation.HasBasicDiscriminatorInformation)
            WriteFactoryMethodBodyForUnionModelForDiscriminatedTypes(codeElement, parentClass, writer);
        else if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForIntersectionType)
            WriteFactoryMethodBodyForIntersectionModel(codeElement, parentClass, parseNodeParameter, writer);
        if (writeDiscriminatorValueRead)
        {
            writer.CloseBlock();
        }
        if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForUnionType || parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForIntersectionType)
        {
            if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForUnionType)
                WriteFactoryMethodBodyForUnionModelForUnDiscriminatedTypes(codeElement, parentClass, parseNodeParameter, writer);
            writer.WriteLine($"return {ResultVarName};");
        }
        else
            writer.WriteLine($"return new {parentClass.Name}();");
    }
    private static readonly Regex factoryMethodIndexParser = new(@"_(?<idx>\d+)", RegexOptions.Compiled, Constants.DefaultRegexTimeout);
    private static void WriteFactoryOverloadMethod(CodeMethod codeElement, CodeClass parentClass, LanguageWriter writer)
    {
        if (int.TryParse(factoryMethodIndexParser.Match(codeElement.Name).Groups["idx"].Value, out var currentDiscriminatorPageIndex) &&
            codeElement.Parameters.FirstOrDefault() is CodeParameter parameter)
        {
            var takeValue = Math.Min(MaxDiscriminatorsPerMethod, parentClass.DiscriminatorInformation.DiscriminatorMappings.Count() - currentDiscriminatorPageIndex * MaxDiscriminatorsPerMethod);
            var currentDiscriminatorPage = parentClass.DiscriminatorInformation.DiscriminatorMappings.Skip(currentDiscriminatorPageIndex * MaxDiscriminatorsPerMethod).Take(takeValue).OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase);
            WriteFactoryMethodBodyForInheritedModel(currentDiscriminatorPage, writer, parameter.Name);
        }
        writer.WriteLine("return null;");
    }
    private const int MaxDiscriminatorsPerMethod = 500;
    private static void WriteSplitFactoryMethodBodyForInheritedModel(CodeClass parentClass, LanguageWriter writer)
    {
        foreach (var otherMethodName in parentClass.Methods.Where(static x => x.IsOverload && x.IsOfKind(CodeMethodKind.Factory))
                                                        .Select(static x => x.Name)
                                                        .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
        {
            var varName = $"{otherMethodName}_result";
            writer.WriteLine($"final {parentClass.Name} {varName} = {otherMethodName}({DiscriminatorMappingVarName});");
            writer.StartBlock($"if ({varName} != null) {{");
            writer.WriteLine($"return {varName};");
            writer.CloseBlock();
        }
    }
    private static void WriteFactoryMethodBodyForInheritedModel(IOrderedEnumerable<KeyValuePair<string, CodeTypeBase>> discriminatorMappings, LanguageWriter writer, string varName = "")
    {
        if (string.IsNullOrEmpty(varName))
            varName = DiscriminatorMappingVarName;
        writer.StartBlock($"switch ({varName}) {{");
        foreach (var mappedType in discriminatorMappings)
        {
            writer.WriteLine($"case \"{mappedType.Key}\": return new {mappedType.Value.AllTypes.First().TypeDefinition?.Name}();");
        }
        writer.CloseBlock();
    }
    private static readonly CodePropertyTypeComparer CodePropertyTypeForwardComparer = new();
    private static readonly CodePropertyTypeComparer CodePropertyTypeBackwardComparer = new(true);
    private void WriteFactoryMethodBodyForIntersectionModel(CodeMethod codeElement, CodeClass parentClass, CodeParameter parseNodeParameter, LanguageWriter writer)
    {
        var includeElse = false;
        var otherProps = parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                    .Where(static x => x.Type is not CodeType propertyType || propertyType.IsCollection || propertyType.TypeDefinition is not CodeClass)
                                    .OrderBy(static x => x, CodePropertyTypeBackwardComparer)
                                    .ThenBy(static x => x.Name)
                                    .ToArray();
        foreach (var property in otherProps.Where(static x => x.Setter != null))
        {
            if (property.Type is CodeType propertyType)
            {
                var deserializationMethodName = $"{parseNodeParameter.Name}.{GetDeserializationMethodName(propertyType, codeElement)}";
                writer.StartBlock($"{(includeElse ? "} else " : string.Empty)}if ({deserializationMethodName} != null) {{");
                writer.WriteLine($"{ResultVarName}.{property.Setter!.Name}({deserializationMethodName});");
                writer.DecreaseIndent();
            }
            if (!includeElse)
                includeElse = true;
        }
        var complexProperties = parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                            .Where(static x => x.Type is CodeType xType && xType.TypeDefinition is CodeClass && !xType.IsCollection)
                                            .Select(static x => new Tuple<CodeProperty, CodeType>(x, (CodeType)x.Type))
                                            .ToArray();
        if (complexProperties.Any())
        {
            if (includeElse)
                writer.StartBlock("} else {");
            foreach (var property in complexProperties.Where(static x => x.Item1.Setter != null))
                writer.WriteLine($"{ResultVarName}.{property.Item1.Setter!.Name}(new {conventions.GetTypeString(property.Item2, codeElement, false)}());");
            if (includeElse)
                writer.CloseBlock();
        }
        else if (otherProps.Any())
            writer.CloseBlock(decreaseIndent: false);
    }
    private const string DiscriminatorMappingVarName = "mappingValue";
    private void WriteFactoryMethodBodyForUnionModelForDiscriminatedTypes(CodeMethod codeElement, CodeClass parentClass, LanguageWriter writer)
    {
        var includeElse = false;
        var otherProps = parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                    .Where(static x => x.Type is CodeType xType && !xType.IsCollection && (xType.TypeDefinition is CodeClass || xType.TypeDefinition is CodeInterface))
                                    .OrderBy(static x => x, CodePropertyTypeForwardComparer)
                                    .ThenBy(static x => x.Name)
                                    .ToArray();
        foreach (var property in otherProps.Where(static x => x.Setter != null))
        {
            if (property.Type is CodeType propertyType && propertyType.TypeDefinition is CodeInterface typeInterface && typeInterface.OriginalClass != null)
                propertyType = new CodeType
                {
                    Name = typeInterface.OriginalClass.Name,
                    TypeDefinition = typeInterface.OriginalClass,
                    CollectionKind = propertyType.CollectionKind,
                    IsNullable = propertyType.IsNullable,
                };
            var mappedType = parentClass.DiscriminatorInformation.DiscriminatorMappings.FirstOrDefault(x => x.Value.Name.Equals(property.Type.Name, StringComparison.OrdinalIgnoreCase));
            writer.StartBlock($"{(includeElse ? "} else " : string.Empty)}if (\"{mappedType.Key}\".equalsIgnoreCase({DiscriminatorMappingVarName})) {{");
            writer.WriteLine($"{ResultVarName}.{property.Setter!.Name}(new {conventions.GetTypeString(property.Type, codeElement, false)}());");
            writer.DecreaseIndent();
            if (!includeElse)
                includeElse = true;
        }
        if (otherProps.Any())
            writer.CloseBlock(decreaseIndent: false);
    }
    private void WriteFactoryMethodBodyForUnionModelForUnDiscriminatedTypes(CodeMethod currentElement, CodeClass parentClass, CodeParameter parseNodeParameter, LanguageWriter writer)
    {
        var includeElse = false;
        var otherProps = parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                    .Where(static x => x.Type is CodeType xType && (xType.IsCollection || xType.TypeDefinition is null || xType.TypeDefinition is CodeEnum))
                                    .OrderBy(static x => x, CodePropertyTypeForwardComparer)
                                    .ThenBy(static x => x.Name)
                                    .ToArray();
        foreach (var property in otherProps.Where(static x => x.Setter != null))
        {
            if (property.Type is CodeType propertyType)
            {
                var serializationMethodName = $"{parseNodeParameter.Name}.{GetDeserializationMethodName(propertyType, currentElement)}";
                writer.StartBlock($"{(includeElse ? "} else " : string.Empty)}if ({serializationMethodName} != null) {{");
                writer.WriteLine($"{ResultVarName}.{property.Setter!.Name}({serializationMethodName});");
                writer.DecreaseIndent();
                if (!includeElse)
                    includeElse = true;
            }
        }
        if (otherProps.Any())
            writer.CloseBlock(decreaseIndent: false);
    }
    private void WriteRequestBuilderBody(CodeClass parentClass, CodeMethod codeElement, LanguageWriter writer)
    {
        var importSymbol = conventions.GetTypeString(codeElement.ReturnType, parentClass);
        conventions.AddRequestBuilderBody(parentClass, importSymbol, writer, pathParameters: codeElement.Parameters.Where(x => x.IsOfKind(CodeParameterKind.Path)));
    }
    private static void AddNullChecks(CodeMethod codeElement, LanguageWriter writer)
    {
        if (!codeElement.IsOverload)
            foreach (var parameter in codeElement.Parameters.Where(static x => !x.Optional && !x.IsOfKind(CodeParameterKind.RequestAdapter, CodeParameterKind.PathParameters)).OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase))
                writer.WriteLine($"Objects.requireNonNull({parameter.Name});");
    }
    private static void WriteRequestBuilderConstructorCall(CodeMethod codeElement, LanguageWriter writer)
    {
        var pathParameters = codeElement.Parameters.Where(static x => x.IsOfKind(CodeParameterKind.Path)).Select(static x => x.Name).ToArray();
        var pathParametersRef = pathParameters.Any() ? (", " + pathParameters.Aggregate((x, y) => $"{x}, {y}")) : string.Empty;
        if (codeElement.Parameters.OfKind(CodeParameterKind.RequestAdapter) is CodeParameter requestAdapterParameter &&
            codeElement.Parameters.OfKind(CodeParameterKind.PathParameters) is CodeParameter urlTemplateParamsParameter)
            writer.WriteLine($"this({urlTemplateParamsParameter.Name}, {requestAdapterParameter.Name}{pathParametersRef});");
    }
    private static void WriteApiConstructorBody(CodeClass parentClass, CodeMethod method, LanguageWriter writer)
    {
        var requestAdapterProperty = parentClass.GetPropertyOfKind(CodePropertyKind.RequestAdapter);
        var pathParametersProperty = parentClass.GetPropertyOfKind(CodePropertyKind.PathParameters);
        var backingStoreParameter = method.Parameters.FirstOrDefault(static x => x.IsOfKind(CodeParameterKind.BackingStore));
        var requestAdapterPropertyName = requestAdapterProperty?.Name ?? string.Empty;
        WriteSerializationRegistration(method.SerializerModules, writer, "registerDefaultSerializer");
        WriteSerializationRegistration(method.DeserializerModules, writer, "registerDefaultDeserializer");
        if (!string.IsNullOrEmpty(method.BaseUrl))
        {
            writer.StartBlock($"if ({requestAdapterPropertyName}.getBaseUrl() == null || {requestAdapterPropertyName}.getBaseUrl().isEmpty()) {{");
            writer.WriteLine($"{requestAdapterPropertyName}.setBaseUrl(\"{method.BaseUrl}\");");
            writer.CloseBlock();
            if (pathParametersProperty != null)
                writer.WriteLine($"{pathParametersProperty.Name}.put(\"baseurl\", {requestAdapterPropertyName}.getBaseUrl());");
        }
        if (backingStoreParameter != null)
            writer.WriteLine($"this.{requestAdapterPropertyName}.enableBackingStore({backingStoreParameter.Name});");
    }
    private static void WriteSerializationRegistration(HashSet<string> serializationModules, LanguageWriter writer, string methodName)
    {
        if (serializationModules != null)
            foreach (var module in serializationModules)
                writer.WriteLine($"ApiClientBuilder.{methodName}({module}.class);");
    }
    private void WriteConstructorBody(CodeClass parentClass, CodeMethod currentMethod, LanguageWriter writer, bool inherits)
    {
        if (inherits)
            if (parentClass.IsOfKind(CodeClassKind.RequestBuilder) &&
                currentMethod.Parameters.OfKind(CodeParameterKind.RequestAdapter) is CodeParameter requestAdapterParameter &&
                parentClass.Properties.FirstOrDefaultOfKind(CodePropertyKind.UrlTemplate) is CodeProperty urlTemplateProperty &&
                !string.IsNullOrEmpty(urlTemplateProperty.DefaultValue))
            {
                var thirdParameterName = string.Empty;
                if (currentMethod.Parameters.OfKind(CodeParameterKind.PathParameters) is CodeParameter pathParametersParameter)
                    thirdParameterName = $", {pathParametersParameter.Name}";
                else if (currentMethod.Parameters.OfKind(CodeParameterKind.RawUrl) is CodeParameter rawUrlParameter)
                    thirdParameterName = $", {rawUrlParameter.Name}";
                writer.WriteLine($"super({requestAdapterParameter.Name}, {urlTemplateProperty.DefaultValue}{thirdParameterName});");
            }
            else
                writer.WriteLine("super();");
        foreach (var propWithDefault in parentClass.GetPropertiesOfKind(CodePropertyKind.BackingStore,
                                                                        CodePropertyKind.RequestBuilder,
                                                                        CodePropertyKind.PathParameters)
                                        .Where(static x => !string.IsNullOrEmpty(x.DefaultValue))
                                        .OrderBy(static x => x.Name))
        {
            writer.WriteLine($"this.{propWithDefault.NamePrefix}{propWithDefault.Name} = {propWithDefault.DefaultValue};");
        }
        foreach (var propWithDefault in parentClass.GetPropertiesOfKind(CodePropertyKind.AdditionalData, CodePropertyKind.Custom) //additional data and custom properties rely on accessors
                                        .Where(static x => !string.IsNullOrEmpty(x.DefaultValue))
                                        // do not apply the default value if the type is composed as the default value may not necessarily which type to use
                                        .Where(static x => x.Type is not CodeType propType || propType.TypeDefinition is not CodeClass propertyClass || propertyClass.OriginalComposedType is null)
                                        .OrderBy(static x => x.Name))
        {
            var setterName = propWithDefault.SetterFromCurrentOrBaseType?.Name is string sName && !string.IsNullOrEmpty(sName) ? sName : $"set{propWithDefault.Name.ToFirstCharacterUpperCase()}";
            var defaultValue = propWithDefault.DefaultValue;
            if (propWithDefault.Type is CodeType propertyType && propertyType.TypeDefinition is CodeEnum enumDefinition)
            {
                defaultValue = $"{enumDefinition.Name}.forValue({defaultValue})";
            }
            writer.WriteLine($"this.{setterName}({defaultValue});");
        }
        if (parentClass.IsOfKind(CodeClassKind.RequestBuilder) &&
            parentClass.GetPropertyOfKind(CodePropertyKind.PathParameters) is CodeProperty pathParametersProp &&
            currentMethod.IsOfKind(CodeMethodKind.Constructor) &&
            currentMethod.Parameters.OfKind(CodeParameterKind.PathParameters) is CodeParameter pathParametersParam)
        {
            var pathParameters = currentMethod.Parameters.Where(static x => x.IsOfKind(CodeParameterKind.Path)).ToArray();
            if (pathParameters.Any())
                conventions.AddParametersAssignment(writer,
                                                    pathParametersParam.Type,
                                                    pathParametersParam.Name,
                                                    $"this.{pathParametersProp.Name}",
                                                    pathParameters
                                                                .Select(static x => (x.Type, string.IsNullOrEmpty(x.SerializationName) ? x.Name : x.SerializationName, x.Name))
                                                                .ToArray());
        }
    }
    private static void WriteSetterBody(CodeMethod codeElement, LanguageWriter writer, CodeClass parentClass)
    {
        if (parentClass.GetBackingStoreProperty() is CodeProperty backingStore)
            writer.WriteLine($"this.get{backingStore.Name.ToFirstCharacterUpperCase()}().set(\"{codeElement.AccessedProperty?.Name}\", value);");
        else
        {
            string value = "PeriodAndDuration".Equals(codeElement.AccessedProperty?.Type?.Name, StringComparison.OrdinalIgnoreCase) ? "PeriodAndDuration.ofPeriodAndDuration(value);" : "value;";
            writer.WriteLine($"this.{codeElement.AccessedProperty?.NamePrefix}{codeElement.AccessedProperty?.Name} = {value}");

        }
    }
    private void WriteGetterBody(CodeMethod codeElement, LanguageWriter writer, CodeClass parentClass)
    {
        var backingStore = parentClass.GetBackingStoreProperty();
        if (backingStore == null || (codeElement.AccessedProperty?.IsOfKind(CodePropertyKind.BackingStore) ?? false))
            writer.WriteLine($"return this.{codeElement.AccessedProperty?.NamePrefix}{codeElement.AccessedProperty?.Name};");
        else
            if (!(codeElement.AccessedProperty?.Type?.IsNullable ?? true) &&
                !(codeElement.AccessedProperty?.ReadOnly ?? true) &&
                !string.IsNullOrEmpty(codeElement.AccessedProperty?.DefaultValue))
        {
            writer.WriteLine($"{conventions.GetTypeString(codeElement.AccessedProperty.Type, codeElement)} value = this.{backingStore.NamePrefix}{backingStore.Name}\");");
            writer.StartBlock("if(value == null) {");
            writer.WriteLines($"value = {codeElement.AccessedProperty.DefaultValue};",
                $"this.set{codeElement.AccessedProperty?.Name.ToFirstCharacterUpperCase()}(value);");
            writer.CloseBlock();
            writer.WriteLine("return value;");
        }
        else
            writer.WriteLine($"return this.get{backingStore.Name.ToFirstCharacterUpperCase()}().get(\"{codeElement.AccessedProperty?.Name}\");");

    }
    private void WriteIndexerBody(CodeMethod codeElement, CodeClass parentClass, LanguageWriter writer, string returnType)
    {
        if (parentClass.GetPropertyOfKind(CodePropertyKind.PathParameters) is CodeProperty pathParametersProperty && codeElement.OriginalIndexer != null)
            conventions.AddParametersAssignment(writer, pathParametersProperty.Type, $"this.{pathParametersProperty.Name}",
                parameters: (codeElement.OriginalIndexer.IndexParameter.Type, codeElement.OriginalIndexer.IndexParameter.SerializationName, codeElement.OriginalIndexer.IndexParameter.Name));
        conventions.AddRequestBuilderBody(parentClass, returnType, writer, conventions.TempDictionaryVarName);
    }
    private void WriteDeserializerBody(CodeMethod codeElement, CodeClass parentClass, LanguageWriter writer, bool inherits)
    {
        if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForUnionType)
            WriteDeserializerBodyForUnionModel(codeElement, parentClass, writer);
        else if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForIntersectionType)
            WriteDeserializerBodyForIntersectionModel(parentClass, writer);
        else
            WriteDeserializerBodyForInheritedModel(codeElement, parentClass, writer, inherits);
    }
    private const string DeserializerReturnType = "HashMap<String, java.util.function.Consumer<ParseNode>>";
    private static void WriteDeserializerBodyForUnionModel(CodeMethod method, CodeClass parentClass, LanguageWriter writer)
    {
        var includeElse = false;
        var otherPropGetters = parentClass
                                .GetPropertiesOfKind(CodePropertyKind.Custom)
                                .Where(static x => !x.ExistsInBaseType)
                                .Where(static x => x.Getter != null && x.Type is CodeType propertyType && !propertyType.IsCollection && propertyType.TypeDefinition is CodeClass)
                                .OrderBy(static x => x, CodePropertyTypeForwardComparer)
                                .ThenBy(static x => x.Name)
                                .Select(static x => x.Getter!.Name)
                                .ToArray();
        foreach (var otherPropGetter in otherPropGetters)
        {
            writer.StartBlock($"{(includeElse ? "} else " : string.Empty)}if (this.{otherPropGetter}() != null) {{");
            writer.WriteLine($"return this.{otherPropGetter}().{method.Name}();");
            writer.DecreaseIndent();
            if (!includeElse)
                includeElse = true;
        }
        if (otherPropGetters.Any())
            writer.CloseBlock(decreaseIndent: false);
        writer.WriteLine($"return new {DeserializerReturnType}();");
    }
    private static void WriteDeserializerBodyForIntersectionModel(CodeClass parentClass, LanguageWriter writer)
    {
        var complexProperties = parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                            .Where(static x => x.Type is CodeType propType && propType.TypeDefinition is CodeClass && !x.Type.IsCollection)
                                            .ToArray();
        if (complexProperties.Any())
        {
            var propertiesNames = complexProperties
                                .Where(static x => x.Getter != null)
                                .Select(static x => x.Getter!.Name)
                                .OrderBy(static x => x)
                                .ToArray();
            var propertiesNamesAsConditions = propertiesNames
                                .Select(static x => $"this.{x}() != null")
                                .Aggregate(static (x, y) => $"{x} || {y}");
            writer.StartBlock($"if ({propertiesNamesAsConditions}) {{");
            var propertiesNamesAsArgument = propertiesNames
                                .Select(static x => $"this.{x}()")
                                .Aggregate(static (x, y) => $"{x}, {y}");
            writer.WriteLine($"return ParseNodeHelper.mergeDeserializersForIntersectionWrapper({propertiesNamesAsArgument});");
            writer.CloseBlock();
        }
        writer.WriteLine($"return new {DeserializerReturnType}();");
    }
    private const string DeserializerVarName = "deserializerMap";
    private void WriteDeserializerBodyForInheritedModel(CodeMethod method, CodeClass parentClass, LanguageWriter writer, bool inherits)
    {
        var fieldToSerialize = parentClass.GetPropertiesOfKind(CodePropertyKind.Custom).ToArray();
        writer.WriteLines(
            $"final {DeserializerReturnType} {DeserializerVarName} = new {DeserializerReturnType}({(inherits ? "super." + method.Name + "()" : fieldToSerialize.Length)});");
        if (fieldToSerialize.Any())
        {
            fieldToSerialize
                    .Where(static x => !x.ExistsInBaseType && x.Setter != null)
                    .OrderBy(static x => x.Name)
                    .Select(x =>
                        $"{DeserializerVarName}.put(\"{x.WireName}\", (n) -> {{ this.{x.Setter!.Name}(n.{GetDeserializationMethodName(x.Type, method)}); }});")
                    .ToList()
                    .ForEach(x => writer.WriteLine(x));
        }
        writer.WriteLine($"return {DeserializerVarName};");
    }
    private const string FactoryMethodName = "createFromDiscriminatorValue";
    private void WriteRequestExecutorBody(CodeMethod codeElement, RequestParams requestParams, CodeClass parentClass, LanguageWriter writer)
    {
        if (codeElement.HttpMethod == null) throw new InvalidOperationException("http method cannot be null");
        var returnType = conventions.GetTypeString(codeElement.ReturnType, codeElement, false);
        WriteGeneratorOrExecutorMethodCall(codeElement, requestParams, parentClass, writer, $"final RequestInformation {RequestInfoVarName} = ", CodeMethodKind.RequestGenerator);
        var sendMethodName = GetSendRequestMethodName(codeElement.ReturnType.IsCollection, returnType, codeElement.ReturnType.AllTypes.First().TypeDefinition is CodeEnum);
        var errorMappingVarName = "null";
        if (codeElement.ErrorMappings.Any())
        {
            errorMappingVarName = "errorMapping";
            writer.WriteLine($"final HashMap<String, ParsableFactory<? extends Parsable>> {errorMappingVarName} = new HashMap<String, ParsableFactory<? extends Parsable>>();");
            foreach (var errorMapping in codeElement.ErrorMappings)
            {
                writer.WriteLine($"{errorMappingVarName}.put(\"{errorMapping.Key.ToUpperInvariant()}\", {errorMapping.Value.Name}::{FactoryMethodName});");
            }
        }
        var factoryParameter = codeElement.ReturnType is CodeType returnCodeType && returnCodeType.TypeDefinition is CodeClass ? $"{returnType}::{FactoryMethodName}" : $"{returnType}.class";
        writer.WriteLine($"return this.requestAdapter.{sendMethodName}({RequestInfoVarName}, {factoryParameter}, {errorMappingVarName});");
    }
    private string GetSendRequestMethodName(bool isCollection, string returnType, bool isEnum)
    {
        if (conventions.PrimitiveTypes.Contains(returnType))
            if (isCollection)
                return "sendPrimitiveCollectionAsync";
            else
                return "sendPrimitiveAsync";
        else if (isEnum)
            if (isCollection)
                return "sendEnumCollectionAsync";
            else
                return "sendEnumAsync";
        else if (isCollection) return "sendCollectionAsync";
        return "sendAsync";
    }
    private const string RequestInfoVarName = "requestInfo";
    private const string RequestConfigVarName = "requestConfig";
    private static void WriteGeneratorOrExecutorMethodCall(CodeMethod codeElement, RequestParams requestParams, CodeClass parentClass, LanguageWriter writer, string prefix, CodeMethodKind codeMethodKind)
    {
        var methodName = parentClass
                                .Methods
                                .FirstOrDefault(x => x.IsOfKind(codeMethodKind) && x.HttpMethod == codeElement.HttpMethod)
                                ?.Name
                                ?.ToFirstCharacterLowerCase();
        var paramsList = new List<CodeParameter?> { requestParams.requestBody, requestParams.requestConfiguration };
        if (requestParams.requestContentType is not null)
            paramsList.Insert(1, requestParams.requestContentType);
        var requestInfoParameters = paramsList.OfType<CodeParameter>()
                                            .Select(static x => x.Name)
                                            .ToList();
        var skipIndex = requestParams.requestBody == null ? 1 : 0;
        requestInfoParameters.AddRange(paramsList.Where(static x => x == null).Skip(skipIndex).Select(static x => "null"));
        var paramsCall = requestInfoParameters.Any() ? requestInfoParameters.Aggregate(static (x, y) => $"{x}, {y}") : string.Empty;
        writer.WriteLine($"{prefix}{methodName}({paramsCall});");
    }
    private void WriteRequestGeneratorBody(CodeMethod codeElement, RequestParams requestParams, CodeClass currentClass, LanguageWriter writer)
    {
        if (codeElement.HttpMethod == null) throw new InvalidOperationException("http method cannot be null");

        writer.WriteLine($"final RequestInformation {RequestInfoVarName} = new RequestInformation();");

        if (requestParams.requestConfiguration != null)
        {
            writer.WriteLine($"if ({requestParams.requestConfiguration.Name} != null) {{");
            writer.IncreaseIndent();
            var requestConfigTypeName = requestParams.requestConfiguration.Type.Name;
            writer.WriteLines($"final {requestConfigTypeName} {RequestConfigVarName} = new {requestConfigTypeName}();",
                        $"{requestParams.requestConfiguration.Name}.accept({RequestConfigVarName});");
            var queryString = requestParams.QueryParameters;
            if (queryString != null)
            {
                var queryStringName = $"{RequestConfigVarName}.{queryString.Name}";
                writer.WriteLine($"{RequestInfoVarName}.addQueryParameters({queryStringName});");
            }
            writer.WriteLines($"{RequestInfoVarName}.headers.putAll({RequestConfigVarName}.headers);",
                             $"{RequestInfoVarName}.addRequestOptions({RequestConfigVarName}.options);");

            writer.CloseBlock();
        }

        writer.WriteLine($"{RequestInfoVarName}.httpMethod = HttpMethod.{codeElement.HttpMethod.ToString()?.ToUpperInvariant()};");
        if (currentClass.GetPropertyOfKind(CodePropertyKind.PathParameters) is CodeProperty urlTemplateParamsProperty &&
            currentClass.GetPropertyOfKind(CodePropertyKind.UrlTemplate) is CodeProperty urlTemplateProperty)
            writer.WriteLines($"{RequestInfoVarName}.urlTemplate = {GetPropertyCall(urlTemplateProperty, "\"\"")};",
                            $"{RequestInfoVarName}.pathParameters = {GetPropertyCall(urlTemplateParamsProperty, "null")};");
        if (codeElement.ShouldAddAcceptHeader)
            writer.WriteLine($"{RequestInfoVarName}.headers.tryAdd(\"Accept\", \"{codeElement.AcceptHeaderValue}\");");

        if (requestParams.requestBody != null &&
            currentClass.GetPropertyOfKind(CodePropertyKind.RequestAdapter) is CodeProperty requestAdapterProperty)
        {
            var toArrayPostfix = requestParams.requestBody.Type.IsCollection ? $".toArray(new {requestParams.requestBody.Type.Name}[0])" : string.Empty;
            var collectionPostfix = requestParams.requestBody.Type.IsCollection ? "Collection" : string.Empty;
            if (requestParams.requestBody.Type.Name.Equals(conventions.StreamTypeName, StringComparison.OrdinalIgnoreCase))
            {
                if (requestParams.requestContentType is not null)
                    writer.WriteLine($"{RequestInfoVarName}.setStreamContent({requestParams.requestBody.Name}, {requestParams.requestContentType.Name});");
                else if (!string.IsNullOrEmpty(codeElement.RequestBodyContentType))
                    writer.WriteLine($"{RequestInfoVarName}.setStreamContent({requestParams.requestBody.Name}, \"{codeElement.RequestBodyContentType}\");");
            }
            else if (requestParams.requestBody.Type is CodeType bodyType && (bodyType.TypeDefinition is CodeClass || bodyType.Name.Equals("MultipartBody", StringComparison.OrdinalIgnoreCase)))
                writer.WriteLine($"{RequestInfoVarName}.setContentFromParsable({requestAdapterProperty.Name}, \"{codeElement.RequestBodyContentType}\", {requestParams.requestBody.Name}{toArrayPostfix});");
            else
                writer.WriteLine($"{RequestInfoVarName}.setContentFromScalar{collectionPostfix}({requestAdapterProperty.Name}, \"{codeElement.RequestBodyContentType}\", {requestParams.requestBody.Name}{toArrayPostfix});");
        }

        writer.WriteLine($"return {RequestInfoVarName};");
    }
    private static string GetPropertyCall(CodeProperty property, string defaultValue) => property == null ? defaultValue : $"{property.Name}";
    private void WriteSerializerBody(CodeClass parentClass, CodeMethod method, LanguageWriter writer, bool inherits)
    {
        if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForUnionType)
            WriteSerializerBodyForUnionModel(parentClass, method, writer);
        else if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForIntersectionType)
            WriteSerializerBodyForIntersectionModel(parentClass, method, writer);
        else
            WriteSerializerBodyForInheritedModel(method, inherits, parentClass, writer);

        var additionalDataProperty = parentClass.GetPropertyOfKind(CodePropertyKind.AdditionalData);

        if (additionalDataProperty != null)
            writer.WriteLine($"writer.writeAdditionalData(this.get{additionalDataProperty.Name.ToFirstCharacterUpperCase()}());");
    }
    private void WriteSerializerBodyForUnionModel(CodeClass parentClass, CodeMethod method, LanguageWriter writer)
    {
        var includeElse = false;
        var otherProps = parentClass
                                .GetPropertiesOfKind(CodePropertyKind.Custom)
                                .Where(static x => !x.ExistsInBaseType && x.Getter != null)
                                .OrderBy(static x => x, CodePropertyTypeForwardComparer)
                                .ThenBy(static x => x.Name)
                                .ToArray();
        foreach (var otherProp in otherProps)
        {
            writer.StartBlock($"{(includeElse ? "} else " : string.Empty)}if (this.{otherProp.Getter!.Name}() != null) {{");
            WriteSerializationMethodCall(otherProp, method, writer, "null");
            writer.DecreaseIndent();
            if (!includeElse)
                includeElse = true;
        }
        if (otherProps.Any())
            writer.CloseBlock(decreaseIndent: false);
    }
    private void WriteSerializerBodyForIntersectionModel(CodeClass parentClass, CodeMethod method, LanguageWriter writer)
    {
        var includeElse = false;
        var otherProps = parentClass
                                .GetPropertiesOfKind(CodePropertyKind.Custom)
                                .Where(static x => !x.ExistsInBaseType && x.Getter != null)
                                .Where(static x => x.Type is not CodeType propertyType || propertyType.IsCollection || propertyType.TypeDefinition is not CodeClass)
                                .OrderBy(static x => x, CodePropertyTypeBackwardComparer)
                                .ThenBy(static x => x.Name)
                                .ToArray();
        foreach (var otherProp in otherProps)
        {
            writer.StartBlock($"{(includeElse ? "} else " : string.Empty)}if (this.{otherProp.Getter!.Name}() != null) {{");
            WriteSerializationMethodCall(otherProp, method, writer, "null");
            writer.DecreaseIndent();
            if (!includeElse)
                includeElse = true;
        }
        var complexProperties = parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                            .Where(static x => x.Type is CodeType propType && propType.TypeDefinition is CodeClass && !x.Type.IsCollection)
                                            .ToArray();
        if (complexProperties.Any())
        {
            if (includeElse)
            {
                writer.WriteLine("} else {");
                writer.IncreaseIndent();
            }
            var propertiesNames = complexProperties
                                .Where(static x => x.Getter != null)
                                .Select(static x => $"this.{x.Getter!.Name}()")
                                .OrderBy(static x => x)
                                .Aggregate(static (x, y) => $"{x}, {y}");
            WriteSerializationMethodCall(complexProperties.First(), method, writer, "null", propertiesNames);
            if (includeElse)
            {
                writer.CloseBlock();
            }
        }
        else if (otherProps.Any())
        {
            writer.CloseBlock(decreaseIndent: false);
        }
    }
    private void WriteSerializerBodyForInheritedModel(CodeMethod method, bool inherits, CodeClass parentClass, LanguageWriter writer)
    {
        if (inherits)
            writer.WriteLine("super.serialize(writer);");
        foreach (var otherProp in parentClass.GetPropertiesOfKind(CodePropertyKind.Custom).Where(static x => !x.ExistsInBaseType && !x.ReadOnly))
            WriteSerializationMethodCall(otherProp, method, writer, $"\"{otherProp.WireName}\"");
    }
    private void WriteSerializationMethodCall(CodeProperty otherProp, CodeMethod method, LanguageWriter writer, string serializationKey, string? dataToSerialize = default)
    {
        if (string.IsNullOrEmpty(dataToSerialize))
            dataToSerialize = $"this.{(otherProp.Getter?.Name is string gName && !string.IsNullOrEmpty(gName) ? gName : "get" + otherProp.Name)}()";
        writer.WriteLine($"writer.{GetSerializationMethodName(otherProp.Type, method)}({serializationKey}, {dataToSerialize});");
    }
    private static readonly BaseCodeParameterOrderComparer parameterOrderComparer = new();
    private string GetFinalReturnType(CodeMethod code, string returnType)
    {
        var voidType = code.IsAsync ? "Void" : "void";
        if (code.ReturnType is CodeType { TypeDefinition: CodeEnum { Flags: true }, IsCollection: false })
            returnType = $"EnumSet<{returnType}>";
        var returnTypeAsyncPrefix = code.IsAsync ? "java.util.concurrent.CompletableFuture<" : string.Empty;
        var returnTypeAsyncSuffix = code.IsAsync ? ">" : string.Empty;
        var reType = returnType.Equals("void", StringComparison.OrdinalIgnoreCase) ? voidType : returnType;
        var collectionCorrectedReturnType = code.ReturnType.IsArray && code.IsOfKind(CodeMethodKind.RequestExecutor) ?
                                            $"Iterable<{returnType.StripArraySuffix()}>" :
                                            reType;
        var isConstructor = code.IsOfKind(CodeMethodKind.Constructor, CodeMethodKind.ClientConstructor, CodeMethodKind.RawUrlConstructor);
        var finalReturnType = isConstructor ? string.Empty : $"{returnTypeAsyncPrefix}{collectionCorrectedReturnType}{returnTypeAsyncSuffix}";
        return finalReturnType.Trim();
    }
    private void WriteMethodPrototype(CodeMethod code, LanguageWriter writer, string returnType)
    {
        var accessModifier = conventions.GetAccessModifier(code.Access);
        var isConstructor = code.IsOfKind(CodeMethodKind.Constructor, CodeMethodKind.ClientConstructor, CodeMethodKind.RawUrlConstructor);
        var methodName = code.Kind switch
        {
            _ when isConstructor && code.Parent != null => code.Parent.Name,
            _ => code.Name
        };
        var parameters = string.Join(", ", code.Parameters.OrderBy(static x => x, parameterOrderComparer).Select(p => conventions.GetParameterSignature(p, code)));
        var reType = returnType.Equals("void", StringComparison.OrdinalIgnoreCase) ? "void" : returnType;
        var collectionCorrectedReturnType = code.ReturnType.IsArray && code.IsOfKind(CodeMethodKind.RequestExecutor) ?
                                            $"Iterable<{returnType.StripArraySuffix()}>" :
                                            reType;
        var staticModifier = code.IsStatic ? " static" : string.Empty;
        conventions.WriteDeprecatedAnnotation(code, writer);
        if (code.Kind is CodeMethodKind.ErrorMessageOverride)
        {
            writer.WriteLine($"@Override");
        }
        if (returnType.Length > 0)
            returnType = $" {returnType}";
        writer.WriteLine($"{accessModifier}{staticModifier}{returnType} {methodName}({parameters}) {{");
    }
    private void WriteMethodDocumentation(CodeMethod code, LanguageWriter writer, string baseReturnType, string finalReturnType)
    {
        var returnVoid = baseReturnType.Equals("void", StringComparison.OrdinalIgnoreCase);
        // Void returns, this includes constructors, should not have a return statement in the JavaDocs.
        var returnRemark = returnVoid ? string.Empty : (code.IsAsync switch
        {
            true => $"@return a CompletableFuture of {baseReturnType}",
            false => $"@return a {finalReturnType}",
        });
        conventions.WriteLongDescription(code,
                                        writer,
                                        code.Parameters
                                            .Where(static x => x.Documentation.DescriptionAvailable)
                                            .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
                                            .Select(x => $"@param {x.Name} {JavaConventionService.RemoveInvalidDescriptionCharacters(x.Documentation.Description)}")
                                            .Union(new[] { returnRemark }));
        if (!returnVoid || code.IsAsync) //Nullable/Nonnull annotations for returns are a part of Method Documentation
            writer.WriteLine(code.ReturnType.IsNullable && !code.IsAsync ? "@jakarta.annotation.Nullable" : "@jakarta.annotation.Nonnull");
    }
    private string GetDeserializationMethodName(CodeTypeBase propType, CodeMethod method)
    {
        var isCollection = propType.CollectionKind != CodeTypeBase.CodeTypeCollectionKind.None;
        var propertyType = conventions.GetTypeString(propType, method, false);
        if (propType is CodeType currentType)
        {
            if (isCollection)
                if (currentType.TypeDefinition == null)
                    return $"getCollectionOfPrimitiveValues({propertyType.ToFirstCharacterUpperCase()}.class)";
                else if (currentType.TypeDefinition is CodeEnum enumType)
                    return $"getCollectionOfEnumValues({enumType.Name}.class)";
                else
                    return $"getCollectionOfObjectValues({propertyType.ToFirstCharacterUpperCase()}::{FactoryMethodName})";
            if (currentType.TypeDefinition is CodeEnum currentEnum)
                return $"getEnum{(currentEnum.Flags ? "Set" : string.Empty)}Value({propertyType.ToFirstCharacterUpperCase()}.class)";
        }
        return propertyType switch
        {
            "byte[]" => "getByteArrayValue()",
            _ when conventions.PrimitiveTypes.Contains(propertyType) => $"get{propertyType}Value()",
            _ => $"getObjectValue({propertyType.ToFirstCharacterUpperCase()}::{FactoryMethodName})",
        };
    }
    private string GetSerializationMethodName(CodeTypeBase propType, CodeMethod method)
    {
        var isCollection = propType.CollectionKind != CodeTypeBase.CodeTypeCollectionKind.None;
        var propertyType = conventions.GetTypeString(propType, method, false);
        if (propType is CodeType currentType)
        {
            if (isCollection)
                if (currentType.TypeDefinition == null)
                    return "writeCollectionOfPrimitiveValues";
                else if (currentType.TypeDefinition is CodeEnum)
                    return "writeCollectionOfEnumValues";
                else
                    return "writeCollectionOfObjectValues";
            if (currentType.TypeDefinition is CodeEnum currentEnum)
                return $"writeEnum{(currentEnum.Flags ? "Set" : string.Empty)}Value";
        }
        return propertyType switch
        {
            "byte[]" => "writeByteArrayValue",
            _ when conventions.PrimitiveTypes.Contains(propertyType) => $"write{propertyType}Value",
            _ => "writeObjectValue",
        };
    }
}

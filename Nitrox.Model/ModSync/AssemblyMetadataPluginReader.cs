#if NET
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Nitrox.Model.Logger;

namespace Nitrox.Model.ModSync;

/// <summary>
///     Reads [BepInPlugin], [BepInDependency] and [BepInIncompatibility] attributes straight from assembly metadata. The
///     assembly is never loaded or executed, so this is safe to run on files downloaded from other players.
/// </summary>
public sealed class AssemblyMetadataPluginReader : IPluginMetadataReader
{
    public static readonly AssemblyMetadataPluginReader Instance = new();

    private const string BEPINEX_NAMESPACE = "BepInEx";

    // BepInDependency.DependencyFlags.HardDependency
    private const int HARD_DEPENDENCY_FLAG = 1;

    public IReadOnlyList<ModPluginInfo> ReadPlugins(string assemblyFullPath, string assemblyRelativePath)
    {
        try
        {
            using FileStream stream = new(assemblyFullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using PEReader peReader = new(stream);
            if (!peReader.HasMetadata)
            {
                return [];
            }
            return ReadPlugins(peReader.GetMetadataReader(), assemblyRelativePath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Debug($"Unable to read plugin metadata from '{assemblyRelativePath}': {ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<ModPluginInfo> ReadPlugins(MetadataReader reader, string assemblyRelativePath)
    {
        List<ModPluginInfo> plugins = [];
        AttributeValueProvider provider = new();
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            string? guid = null, name = null, version = null;
            List<ModDependencyInfo> dependencies = [];
            List<string> incompatibilities = [];

            foreach (CustomAttributeHandle attributeHandle in type.GetCustomAttributes())
            {
                CustomAttribute attribute = reader.GetCustomAttribute(attributeHandle);
                string attributeName = GetAttributeTypeName(reader, attribute);
                if (attributeName is not ("BepInPlugin" or "BepInDependency" or "BepInIncompatibility"))
                {
                    continue;
                }

                CustomAttributeValue<object?> value;
                try
                {
                    value = attribute.DecodeValue(provider);
                }
                catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException or InvalidOperationException)
                {
                    continue;
                }
                ImmutableArray<CustomAttributeTypedArgument<object?>> args = value.FixedArguments;

                switch (attributeName)
                {
                    case "BepInPlugin" when args.Length >= 3:
                        guid = args[0].Value as string;
                        name = args[1].Value as string;
                        version = args[2].Value as string;
                        break;
                    case "BepInDependency" when args.Length >= 1 && args[0].Value is string dependencyGuid:
                        // Constructors: (string guid, DependencyFlags flags = Hard) and (string guid, string minimumVersion).
                        string minimumVersion = args.Length >= 2 && args[1].Value is string minVersion ? minVersion : "";
                        bool isHard = args.Length < 2 || args[1].Value is string || (args[1].Value is int flags && (flags & HARD_DEPENDENCY_FLAG) != 0);
                        dependencies.Add(new ModDependencyInfo(dependencyGuid, minimumVersion, isHard));
                        break;
                    case "BepInIncompatibility" when args.Length >= 1 && args[0].Value is string incompatibleGuid:
                        incompatibilities.Add(incompatibleGuid);
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(guid))
            {
                plugins.Add(new ModPluginInfo(guid!, name ?? guid!, version ?? "", assemblyRelativePath, dependencies, incompatibilities));
            }
        }
        return plugins;
    }

    private static string GetAttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle typeHandle = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default
        };
        switch (typeHandle.Kind)
        {
            case HandleKind.TypeReference:
                TypeReference typeReference = reader.GetTypeReference((TypeReferenceHandle)typeHandle);
                return reader.GetString(typeReference.Namespace) == BEPINEX_NAMESPACE ? reader.GetString(typeReference.Name) : "";
            case HandleKind.TypeDefinition:
                TypeDefinition typeDefinition = reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
                return reader.GetString(typeDefinition.Namespace) == BEPINEX_NAMESPACE ? reader.GetString(typeDefinition.Name) : "";
            default:
                return "";
        }
    }

    /// <summary>
    ///     Minimal attribute decoder: strings stay strings and every enum is treated as Int32 (true for BepInEx's DependencyFlags).
    /// </summary>
    private sealed class AttributeValueProvider : ICustomAttributeTypeProvider<object?>
    {
        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;
        public object? GetSystemType() => typeof(Type);
        public object? GetSZArrayType(object? elementType) => elementType;
        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => PrimitiveTypeCode.Int32;
        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => PrimitiveTypeCode.Int32;
        public object? GetTypeFromSerializedName(string name) => PrimitiveTypeCode.Int32;
        public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(object? type) => type is Type;
    }
}
#endif

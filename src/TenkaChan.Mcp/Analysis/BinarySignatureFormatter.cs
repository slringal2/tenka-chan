using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace TenkaChan.Mcp.Analysis;

/// <summary>Formats PE metadata only. Never loads a target assembly or executes its code.</summary>
internal sealed class BinarySignatureFormatter : ISignatureTypeProvider<string, int>
{
    public string TypeName(MetadataReader reader, TypeDefinitionHandle handle, int depth = 0)
    {
        if (depth > 32) throw new BadImageFormatException("Excessive nested type depth.");
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var parent = type.GetDeclaringType();
        if (!parent.IsNil) return TypeName(reader, parent, depth + 1) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private string ReferenceName(MetadataReader reader, TypeReferenceHandle handle, int depth)
    {
        if (depth > 32) throw new BadImageFormatException("Excessive nested type reference depth.");
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return ReferenceName(reader, (TypeReferenceHandle)type.ResolutionScope, depth + 1) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string Method(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        var signature = method.DecodeSignature(this, 0);
        return FormatMethod(TypeName(reader, method.GetDeclaringType()), reader.GetString(method.Name), signature);
    }

    private static string FormatMethod(string type, string name, MethodSignature<string> sig) =>
        sig.ReturnType + " " + type + "::" + name +
        (sig.GenericParameterCount > 0 ? "``" + sig.GenericParameterCount : "") +
        "(" + string.Join(", ", sig.ParameterTypes) + ")";

    public string Entity(MetadataReader reader, EntityHandle handle, int depth = 0)
    {
        if (depth > 32) throw new BadImageFormatException("Excessive metadata reference depth.");
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition: return TypeName(reader, (TypeDefinitionHandle)handle);
            case HandleKind.TypeReference: return ReferenceName(reader, (TypeReferenceHandle)handle, depth);
            case HandleKind.TypeSpecification:
                return reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, depth + 1);
            case HandleKind.MethodDefinition: return Method(reader, (MethodDefinitionHandle)handle);
            case HandleKind.MemberReference:
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                var owner = Entity(reader, member.Parent, depth + 1);
                return member.GetKind() == MemberReferenceKind.Method
                    ? FormatMethod(owner, reader.GetString(member.Name), member.DecodeMethodSignature(this, depth + 1))
                    : member.DecodeFieldSignature(this, depth + 1) + " " + owner + "::" + reader.GetString(member.Name);
            }
            case HandleKind.MethodSpecification:
            {
                var method = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return Entity(reader, method.Method, depth + 1) + " <" +
                    string.Join(", ", method.DecodeSignature(this, depth + 1)) + ">";
            }
            case HandleKind.FieldDefinition:
            {
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                return field.DecodeSignature(this, depth + 1) + " " +
                    TypeName(reader, field.GetDeclaringType()) + "::" + reader.GetString(field.Name);
            }
            case HandleKind.ModuleReference:
                return reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)handle).Name);
            case HandleKind.StandaloneSignature:
                return "signature 0x" + MetadataTokens.GetToken(handle).ToString("X8");
            default: return "metadata 0x" + MetadataTokens.GetToken(handle).ToString("X8");
        }
    }

    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[" + new string(',', Math.Clamp(shape.Rank - 1, 0, 32)) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetPinnedType(string elementType) => elementType + " pinned";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> arguments) =>
        genericType + "<" + string.Join(", ", arguments) + ">";
    public string GetGenericMethodParameter(int context, int index) => "!!" + index;
    public string GetGenericTypeParameter(int context, int index) => "!" + index;
    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        "methodptr(" + string.Join(", ", signature.ParameterTypes) + ")->" + signature.ReturnType;
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeName(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => ReferenceName(reader, handle, 0);
    public string GetTypeFromSpecification(MetadataReader reader, int context, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        if (context > 32) throw new BadImageFormatException("Excessive signature depth.");
        return reader.GetTypeSpecification(handle).DecodeSignature(this, context + 1);
    }
    public string GetPrimitiveType(PrimitiveTypeCode code) => code switch
    {
        PrimitiveTypeCode.Void => "void", PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char", PrimitiveTypeCode.SByte => "sbyte", PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short", PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int", PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long", PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float", PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string", PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "nint", PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "typedref", _ => code.ToString()
    };
}

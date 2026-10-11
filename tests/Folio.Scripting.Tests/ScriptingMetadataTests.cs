using System.Reflection;

namespace Folio.Scripting.Tests;

public class ScriptingMetadataTests
{
    [Fact]
    public void FolioScriptingDoesNotExposeJintTypesInPublicSurface()
    {
        var scriptingAssembly = typeof(IScriptEngine).Assembly;

        var jintViolations = new List<string>();

        foreach (var type in scriptingAssembly.GetExportedTypes())
        {
            if (type.BaseType is { } baseType && IsJintType(baseType))
                jintViolations.Add($"{type.FullName} inherits from {baseType.FullName}");

            foreach (var iface in type.GetInterfaces())
            {
                if (IsJintType(iface))
                    jintViolations.Add($"{type.FullName} implements {iface.FullName}");
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var prop in type.GetProperties(flags))
            {
                if (IsJintType(prop.PropertyType))
                    jintViolations.Add($"{type.FullName}.{prop.Name} property type is {prop.PropertyType.FullName}");
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (IsJintType(method.ReturnType))
                    jintViolations.Add($"{type.FullName}.{method.Name} returns {method.ReturnType.FullName}");

                foreach (var par in method.GetParameters())
                {
                    if (IsJintType(par.ParameterType))
                        jintViolations.Add($"{type.FullName}.{method.Name} parameter {par.Name} is {par.ParameterType.FullName}");
                }
            }

            foreach (var field in type.GetFields(flags))
            {
                if (IsJintType(field.FieldType))
                    jintViolations.Add($"{type.FullName}.{field.Name} field is {field.FieldType.FullName}");
            }
        }

        Assert.Empty(jintViolations);
    }

    [Fact]
    public void FolioScriptingDoesNotReferenceJint()
    {
        var referencedAssemblies = typeof(IScriptEngine).Assembly.GetReferencedAssemblies();
        var jintRef = referencedAssemblies.FirstOrDefault(a => a.Name?.StartsWith("Jint", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Null(jintRef);
    }

    private static bool IsJintType(Type? type)
    {
        var visited = new HashSet<Type>();
        return Check(type, visited);

        static bool Check(Type? t, HashSet<Type> visited)
        {
            if (t is null || !visited.Add(t))
                return false;

            while (t.HasElementType)
            {
                t = t.GetElementType()!;
                if (!visited.Add(t))
                    return false;
            }

            if (t.IsGenericType)
            {
                if (Check(t.GetGenericTypeDefinition(), visited))
                    return true;
                foreach (var arg in t.GetGenericArguments())
                {
                    if (Check(arg, visited))
                        return true;
                }
            }

            return t.Namespace?.StartsWith("Jint", StringComparison.OrdinalIgnoreCase) == true ||
                   t.Assembly.GetName().Name?.StartsWith("Jint", StringComparison.OrdinalIgnoreCase) == true;
        }
    }
}

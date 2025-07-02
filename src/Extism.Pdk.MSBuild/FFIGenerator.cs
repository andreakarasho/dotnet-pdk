using Mono.Cecil;

using System.Text;

namespace Extism.Pdk.MSBuild
{
    /// <summary>
    /// Generate the necessary glue code to export/import .NET functions
    /// </summary>
    public class FFIGenerator
    {
        private readonly Action<string> _logError;
        private readonly string _extism;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="extism"></param>
        /// <param name="logError"></param>
        public FFIGenerator(string extism, Action<string> logError)
        {
            _logError = logError;
            _extism = extism;
        }

        /// <summary>
        /// Generate glue code for the given assembly and referenced assemblies
        /// </summary>
        /// <param name="assembly"></param>
        /// <param name="directory"></param>
        /// <returns></returns>
        public IEnumerable<FileEntry> GenerateGlueCode(AssemblyDefinition assembly, string directory)
        {
            var assemblies = assembly.MainModule.AssemblyReferences
                .Where(r => !r.Name.StartsWith("System") && !r.Name.StartsWith("Microsoft") && r.Name != "Extism.Pdk")
                .Select(r => AssemblyDefinition.ReadAssembly(Path.Combine(directory, r.Name + ".dll")))
                .ToList();

            assemblies.Add(assembly);

            var types = assemblies.SelectMany(a => a.MainModule.Types).ToArray();

            // TODO: also find F# module functions
            var importedMethods = types
                .SelectMany(t => t.Methods)
                .Where(m => m.HasPInvokeInfo)
                .ToArray();

            var files = GenerateImports(importedMethods, _extism);

            return files;
        }

        private List<FileEntry> GenerateImports(MethodDefinition[] importedMethods, string extism)
        {
            var modules = importedMethods.GroupBy(m => m.PInvokeInfo.Module.Name)
                            .Select(g => new
                            {
                                Name = g.Key,
                                Imports = g.Select(m => ToImportStatement(m)).ToArray(),
                            })
                            .ToList();

            var extismWritten = false;

            var files = new List<FileEntry>();

            // For DllImport to work with wasm, the name of the file has to match
            // the name of the module that the function is imported from
            foreach (var module in modules)
            {
                var builder = new StringBuilder();

                if (module.Name == "extism")
                {
                    extismWritten = true;
                    builder.AppendLine(extism);
                }
                else
                {
                    builder.AppendLine(Preamble);
                }

                foreach (var import in module.Imports)
                {
                    builder.AppendLine(import);
                }

                files.Add(new FileEntry { Name = $"{module.Name}.c", Content = builder.ToString() });
            }

            if (!extismWritten)
            {
                files.Add(new FileEntry { Name = $"extism.c", Content = extism });
            }

            return files;
        }

        private string ToImportStatement(MethodDefinition method)
        {
            var moduleName = method.PInvokeInfo.Module.Name;
            if (moduleName == "extism")
            {
                // Redirect imported host functions to extism:host/user
                // The PDK functions don't use this generator, so we can safely assume
                // every `extism` import is a user host function
                moduleName = "extism:host/user";
            }

            var functionName = string.IsNullOrEmpty(method.PInvokeInfo.EntryPoint) ? method.Name : method.PInvokeInfo.EntryPoint;

            if (!_types.ContainsKey(method.ReturnType.Name))
            {
                _logError($"Unsupported return type: {method.ReturnType.FullName} on {method.FullName} method.");
                return "";
            }

            var sb = new StringBuilder();
            var p = method.Parameters.FirstOrDefault(p => !_types.ContainsKey(p.ParameterType.Name));
            if (p != null)
            {
                _logError($"Unsupported parameter type: {p.Name} ({p.ParameterType.FullName}) on {method.FullName} method.");

                return $"\\\\ Unrecognized type: ${p.ParameterType.Name} => '{p.ParameterType.FullName}'.";
            }

            var parameters = string.Join(", ", method.Parameters.Select(p => $"{_types[p.ParameterType.Name]} {p.Name}"));
            var parameterNames = string.Join(", ", method.Parameters.Select(p => p.Name));

            var returnKeyword = _types[method.ReturnType.Name] == "void" ? "" : "return ";

            return $$"""
            IMPORT("{{moduleName}}", "{{functionName}}") extern {{_types[method.ReturnType.Name]}} {{functionName}}_import({{parameters}});
            
            {{_types[method.ReturnType.Name]}} {{functionName}}({{parameters}}) {
                {{returnKeyword}}{{functionName}}_import({{parameterNames}});
            }
            """;
        }

        private const string Preamble = """
#include <stdint.h>
#include <assert.h>
#include <stdbool.h>

#define IMPORT(a, b) __attribute__((import_module(a), import_name(b)))

typedef uint64_t ExtismPointer;
""";

        private static readonly Dictionary<string, string> _types = new Dictionary<string, string>
        {
            { nameof(SByte), "int8_t" },
            { nameof(Int16), "int16_t" },
            { nameof(Int32), "int32_t" },
            { nameof(Int64), "int64_t" },

            { nameof(Byte), "uint8_t" },
            { nameof(UInt16), "uint16_t" },
            { nameof(UInt32), "uint32_t" },
            { nameof(UInt64), "uint64_t" },

            { nameof(Single), "float" },
            { nameof(Double), "double" },

            { "Void", "void"},
        };
    }

    /// <summary>
    /// A file generated by the task
    /// </summary>
    public class FileEntry
    {
        /// <summary>
        /// Name of the file
        /// </summary>
        public string Name { get; set; } = default!;

        /// <summary>
        /// Content of the file
        /// </summary>
        public string Content { get; set; } = default!;
    }
}

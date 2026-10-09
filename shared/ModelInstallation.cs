namespace SheepCode.Distribution;

internal sealed record InstalledFile(string Name, string Path, bool Present, bool ParentPresent, long? Bytes, long? ExpectedBytes, bool? SizeMatches, bool Partial, string? Error);
internal sealed record ModelInstallation(bool Configured, bool FilesReady, string State, string Message, IReadOnlyList<InstalledFile> Files);
internal sealed record CatalogModelState(string Id, string Label, long Size, bool Selected, string State, string File, bool Present, bool Partial, long? ActualBytes);

// File presence is a startup prerequisite, not proof of a loaded model or a valid SHA-256.
internal static class ModelInstallationInspector
{
    internal static InstalledFile FileStatus(string root, string name, string path, long? expectedBytes = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(name, "", false, false, null, expectedBytes, null, false, "Ruta sin configurar.");
        try
        {
            var full = InstallationPaths.Resolve(root, path);
            var present = System.IO.File.Exists(full);
            long? bytes = present ? new FileInfo(full).Length : null;
            return new(name, full, present, Directory.Exists(System.IO.Path.GetDirectoryName(full)), bytes, expectedBytes,
                present && expectedBytes is { } expected ? bytes == expected : null, System.IO.File.Exists(full + ".part"), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(name, path, false, false, null, expectedBytes, null, false, e.Message); }
    }

    internal static ModelInstallation Inspect(string root, RuntimeProfile profile)
    {
        if (!profile.Configured) return new(false, false, "unconfigured",
            "No hay un modelo seleccionado. Usa Modelos → Analizar → Instalar recomendación. Si los pesos ya están en la ruta esperada, la instalación verifica y reutiliza el archivo.", []);
        var files = new List<InstalledFile>();
        if (profile.NativeExecutable)
        {
            files.Add(FileStatus(root, "ejecutable del motor", profile.Executable));
            var asset = ModelCatalog.Models.FirstOrDefault(m => m.Id == profile.ModelId);
            files.Add(FileStatus(root, "pesos del modelo", profile.ModelFile, asset?.Size));
            if (profile.Kind == "llama" && InstallationPaths.Resolve(root, profile.Executable).Equals(SafeFiles.Child(root, @"runtime\llama\llama-server.exe"), StringComparison.OrdinalIgnoreCase))
            {
                var runtime = @"runtime\llama";
                foreach (var dependency in new[] { "llama-server-impl.dll", "llama-common.dll", "llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu-x64.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
                    files.Add(FileStatus(root, "biblioteca del motor " + dependency, System.IO.Path.Combine(runtime, dependency)));
                if (profile.Devices != "none" || profile.GpuLayers > 0) files.Add(FileStatus(root, "biblioteca Vulkan del motor", System.IO.Path.Combine(runtime, "ggml-vulkan.dll")));
            }
        }
        else
        {
            var paths = InstallationPaths.Load(root);
            files.Add(FileStatus(root, "Python de Strata", paths.StrataPython));
            files.Add(FileStatus(root, "servidor de Strata", @"runtime\Strata\serve\server.py"));
            files.Add(FileStatus(root, "configuración de Strata", profile.StrataConfig));
        }
        if (profile.RequireRx580Voice)
        {
            files.Add(FileStatus(root, "Python de voz RX 580", InstallationPaths.Load(root).VoicePython));
            files.Add(FileStatus(root, "configuración de voz RX 580", @"state\tts-config.json"));
        }
        var missing = files.FirstOrDefault(f => !f.Present || f.SizeMatches == false || f.Error is not null);
        if (missing is null) return new(true, true, "files_present",
            "Los archivos necesarios están presentes. Pulsa Cargar IA para iniciar el motor; este diagnóstico no comprueba inferencia ni SHA-256.", files);
        var instruction = missing.Name.Contains("pesos", StringComparison.Ordinal)
            ? " Reintenta la instalación del modelo seleccionado; conserva los pesos existentes y las descargas .part."
            : missing.Name.Contains("voz", StringComparison.Ordinal)
                ? " Restaura el paquete neuronal RX 580 original; no se sustituye por otra voz."
                : " Usa «Repara el motor» para restaurar el runtime nativo desde el paquete local o vuelve a ejecutar el setup; conserva modelos y ajustes.";
        var message = missing.Error is not null ? "No se pudo comprobar " + missing.Name + ": " + missing.Path + ". " + missing.Error :
            missing.Partial && !missing.Present ? "La descarga de " + missing.Name + " está incompleta: " + missing.Path + ".part" :
            !missing.ParentPresent ? "No existe la carpeta de " + missing.Name + ": " + System.IO.Path.GetDirectoryName(missing.Path) :
            !missing.Present ? "No existe el archivo de " + missing.Name + ": " + missing.Path :
            "El tamaño de " + missing.Name + " no coincide con el catálogo: " + missing.Path + $" ({missing.Bytes} de {missing.ExpectedBytes} bytes).";
        return new(true, false, "incomplete", message + instruction, files);
    }

    internal static IReadOnlyList<CatalogModelState> Catalog(string root, RuntimeProfile profile)
    {
        var folder = InstallationPaths.Resolve(root, InstallationPaths.Load(root).Models);
        return ModelCatalog.Models.OrderByDescending(m => m.Id == profile.ModelId).Select(m =>
        {
            var selected = m.Id == profile.ModelId;
            var path = selected && profile.NativeExecutable ? profile.ModelFile : System.IO.Path.Combine(folder, m.Id, m.File);
            var file = FileStatus(root, "pesos del modelo", path, m.Size);
            var state = selected ? !file.Present ? "seleccionado; faltan los pesos" : file.SizeMatches == false ? "seleccionado; archivo incompleto o de tamaño distinto" : "seleccionado; consulta engine.ready para saber si está cargado" :
                file.Present ? file.SizeMatches == false ? "archivo presente; tamaño distinto; sin seleccionar" : "archivo presente; sin seleccionar; SHA-256 sin comprobar" :
                file.Partial ? "descarga incompleta; sin seleccionar" : "catálogo; sin descargar ni seleccionar";
            return new CatalogModelState(m.Id, m.Label, m.Size, selected, state, file.Path, file.Present, file.Partial, file.Bytes);
        }).ToArray();
    }
}
using SheepCode.Distribution;

namespace SheepCode;

internal static class SetupProgram
{
    [STAThread] private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) return SetupDiagnostics.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--gpu-ui-check")) return SetupDiagnostics.GpuUiCheck();
        if (args.Contains("--install-test"))
        {
            try
            {
                var i = Array.IndexOf(args, "--target"); if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Falta --target");
                var root = Path.GetFullPath(args[i + 1]); var models = Path.Combine(root, "models");
                InstallPlan plan;
                if (args.Contains("--reuse-strata")) plan = ModelCatalog.Reuse(ModelCatalog.FindExisting() ?? throw new IOException("No hay Strata para reutilizar."));
                else if (args.Contains("--small-profile"))
                {
                    var model = ModelCatalog.Models.First(m => m.Id == "qwen3-0.6b"); plan = new(model.Id, model.Label, "llama", "Perfil instalado para validación funcional del setup; sin comparativas de rendimiento.", model.Size, 4096, model);
                }
                else plan = ModelCatalog.EditorOnly();
                Installer.InstallAsync(new(root, models, plan, false, false, false, false), null, CancellationToken.None).GetAwaiter().GetResult(); return 0;
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "install-test-error.txt"), e.ToString()); return 1; }
        }
        using var form = new SetupForm(args.Contains("--preview")); Application.Run(form); return 0;
    }
}

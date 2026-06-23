using System.CommandLine;

namespace KSPTextureBundler;

internal static class Program
{
    static int Main(string[] args)
    {
        var root = new RootCommand(
            "Build KSPTextureLoader-compatible Unity asset bundles from DDS/PNG textures."
        );
        root.Subcommands.Add(BuildCommand());
        root.Subcommands.Add(ExtractCommand());
        root.Subcommands.Add(MakeSeedCommand());
        foreach (var dev in DevCommands())
            root.Subcommands.Add(dev);

        return root.Parse(args).Invoke();
    }

    static Command BuildCommand()
    {
        var output = new Option<string>("--output", "-o")
        {
            Description = "Output .bundle path.",
            Required = true,
        };
        var name = new Option<string?>("--name", "-n")
        {
            Description = "AssetBundle name (default: output file name without extension).",
        };
        var seed = new Option<string?>("--seed")
        {
            Description = "Override the embedded type-tree seed bundle.",
        };
        var prefix = new Option<string?>("--prefix")
        {
            Description = "A path prefix prepended to every texture path.",
        };
        var inputs = new Argument<string[]>("inputs")
        {
            Description = "A directory containing the texture files",
            Arity = ArgumentArity.OneOrMore,
        };

        var cmd = new Command("build", "Build an asset bundle from texture files.");
        cmd.Options.Add(output);
        cmd.Options.Add(name);
        cmd.Options.Add(seed);
        cmd.Options.Add(prefix);
        cmd.Arguments.Add(inputs);
        cmd.SetAction(pr =>
            Commands.Build(
                pr.GetValue(inputs) ?? [],
                pr.GetValue(output)!,
                pr.GetValue(name),
                pr.GetValue(seed),
                pr.GetValue(prefix)
            )
        );
        return cmd;
    }

    static Command ExtractCommand()
    {
        var bundle = new Argument<string>("bundle") { Description = "The bundle to extract from." };
        var output = new Option<string>("--output", "-o")
        {
            Description = "Output directory for the extracted .dds files.",
            Required = true,
        };
        var flat = new Option<bool>("--flat")
        {
            Description =
                "Write every texture as <name>.dds in the output directory instead of "
                + "recreating the container path tree.",
        };

        var cmd = new Command("extract", "Extract a bundle's textures as DDS files.");
        cmd.Arguments.Add(bundle);
        cmd.Options.Add(output);
        cmd.Options.Add(flat);
        cmd.SetAction(pr =>
            Commands.Extract(pr.GetValue(bundle)!, pr.GetValue(output)!, pr.GetValue(flat))
        );
        return cmd;
    }

    static Command MakeSeedCommand()
    {
        var source = new Option<string>("--source")
        {
            Description =
                "A Unity 2019.x bundle containing a Texture2D (28) and AssetBundle (142) "
                + "with type trees, used as the schema source.",
            Required = true,
        };
        var output = new Option<string>("--output", "-o")
        {
            Description = "Output seed .bundle path.",
            Required = true,
        };

        var cmd = new Command(
            "make-seed",
            "Create a type-tree seed bundle (schema + 1x1 placeholder, no source content)."
        );
        cmd.Options.Add(source);
        cmd.Options.Add(output);
        cmd.SetAction(pr => Commands.MakeSeed(pr.GetValue(source)!, pr.GetValue(output)!));
        return cmd;
    }

    static IEnumerable<Command> DevCommands()
    {
        var probeArg = new Argument<string>("bundle") { Description = "Bundle to inspect." };
        var probe = new Command("dev-probe", "Dump a bundle's directory and type trees.")
        {
            Hidden = true,
        };
        probe.Arguments.Add(probeArg);
        probe.SetAction(pr => Commands.Probe(pr.GetValue(probeArg)!));
        yield return probe;

        var valBundle = new Argument<string>("bundle") { Description = "Bundle to validate." };
        var valInputs = new Argument<string[]>("inputs")
        {
            Description = "The same inputs used to build the bundle.",
            Arity = ArgumentArity.OneOrMore,
        };
        var valPrefix = new Option<string?>("--prefix")
        {
            Description = "Same --prefix used to build.",
        };
        var validate = new Command(
            "dev-validate",
            "Verify a bundle's streamed bytes are byte-exact with the source textures."
        )
        {
            Hidden = true,
        };
        validate.Arguments.Add(valBundle);
        validate.Arguments.Add(valInputs);
        validate.Options.Add(valPrefix);
        validate.SetAction(pr =>
            Commands.Validate(
                pr.GetValue(valBundle)!,
                pr.GetValue(valInputs) ?? [],
                pr.GetValue(valPrefix)
            )
        );
        yield return validate;

        var paletteTest = new Command(
            "dev-palette-test",
            "Self-check Kopernicus palette conversion against the loader's decode math."
        )
        {
            Hidden = true,
        };
        paletteTest.SetAction(_ => Commands.PaletteTest());
        yield return paletteTest;
    }
}

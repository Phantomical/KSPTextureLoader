using System.CommandLine;

namespace KSPTextureUtils;

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
        root.Subcommands.Add(DevCommand());

        return root.Parse(args).Invoke();
    }

    static Command BuildCommand()
    {
        var output = new Option<string>("--output", "-o")
        {
            Description = "Path that the output bundle will be written to.",
            Required = true,
        };
        var name = new Option<string?>("--name", "-n")
        {
            Description = "Override the internal name of the asset bundle",
        };
        var seed = new Option<string?>("--seed")
        {
            Description = "Override the embedded type-tree seed bundle.",
        };
        var prefix = new Option<string?>("--prefix")
        {
            Description = "A path prefix prepended to every texture path.",
        };
        var mipmapStreaming = new Option<bool>("--mipmap-streaming")
        {
            Description = "Enabled mipmap streaming on bundled textures.",
        };
        var properties = new Option<string?>("--properties")
        {
            Description =
                "A YAML file assigning per-texture properties (readable, mipmapStreaming) "
                + "to input files matched by glob.",
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
        cmd.Options.Add(mipmapStreaming);
        cmd.Options.Add(properties);
        cmd.Arguments.Add(inputs);
        cmd.SetAction(pr =>
            Commands.Build(
                pr.GetValue(inputs) ?? [],
                pr.GetValue(output)!,
                pr.GetValue(name),
                pr.GetValue(seed),
                pr.GetValue(prefix),
                pr.GetValue(mipmapStreaming),
                pr.GetValue(properties)
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
            Description = "Write textures directly to the output directory",
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

    static Command MakeTypeTreeCommand()
    {
        var seed = new Option<string?>("--seed")
        {
            Description = "Override the embedded type-tree seed bundle.",
        };
        var output = new Option<string>("--output", "-o")
        {
            Description = "Output type-tree bundle path.",
            Required = true,
        };

        var cmd = new Command(
            "make-typetree",
            "Create a reference type-tree bundle (all texture type trees + AssetBundle "
                + "scaffolding, no objects) for the runtime loader to embed."
        );
        cmd.Options.Add(seed);
        cmd.Options.Add(output);
        cmd.SetAction(pr => Commands.MakeTypeTree(pr.GetValue(seed), pr.GetValue(output)!));
        return cmd;
    }

    // Developer-only inspection and self-check verbs, grouped under a single hidden
    // `dev` parent command (e.g. `dev probe`, `dev validate`, `dev palette-test`,
    // `dev cubemap-test`) so they stay out of the way of the normal build/extract
    // workflow.
    static Command DevCommand()
    {
        var dev = new Command("dev", "Developer-only inspection and self-check commands.")
        {
            Hidden = true,
        };
        dev.Subcommands.Add(ProbeCommand());
        dev.Subcommands.Add(ValidateCommand());
        dev.Subcommands.Add(MakeTypeTreeCommand());
        dev.Subcommands.Add(PaletteTestCommand());
        dev.Subcommands.Add(CubemapTestCommand());
        // `dev` is hidden, so System.CommandLine renders nothing for `dev -h`;
        // `dev help` lists the group's subcommands explicitly instead.
        dev.Subcommands.Add(HelpCommand(dev));
        return dev;
    }

    static Command HelpCommand(Command dev)
    {
        var help = new Command("help", "List the dev subcommands.");
        help.SetAction(_ =>
        {
            Console.WriteLine(dev.Description);
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  ksp-texture-util dev <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            int width = dev.Subcommands.Max(c => c.Name.Length);
            foreach (var sub in dev.Subcommands)
                Console.WriteLine($"  {sub.Name.PadRight(width)}  {sub.Description}");
        });
        return help;
    }

    static Command ProbeCommand()
    {
        var probeArg = new Argument<string>("bundle") { Description = "Bundle to inspect." };
        var probe = new Command("probe", "Dump a bundle's directory and type trees.");
        probe.Arguments.Add(probeArg);
        probe.SetAction(pr => Commands.Probe(pr.GetValue(probeArg)!));
        return probe;
    }

    static Command ValidateCommand()
    {
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
            "validate",
            "Verify a bundle's streamed bytes are byte-exact with the source textures."
        );
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
        return validate;
    }

    static Command PaletteTestCommand()
    {
        var paletteTest = new Command(
            "palette-test",
            "Self-check Kopernicus palette conversion against the loader's decode math."
        );
        paletteTest.SetAction(_ => Commands.PaletteTest());
        return paletteTest;
    }

    static Command CubemapTestCommand()
    {
        var cubemapTest = new Command(
            "cubemap-test",
            "Self-check 2D cross -> cubemap face mapping and block alignment."
        );
        cubemapTest.SetAction(_ => Commands.CubemapTest());
        return cubemapTest;
    }
}

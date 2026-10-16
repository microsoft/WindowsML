// Copyright (C) Microsoft Corporation. All rights reserved.

using Windows.ApplicationModel;
using Windows.Management.Deployment;

const string mainName = "OptionalMsixMinimal.Main";
const string optionalName = "OptionalMsixMinimal.Optional";
const string publisher = "CN=OptionalMsixMinimal";
const string usage = "Usage:\n  optional-msix.exe install <path-to-Optional.msix>\n  optional-msix.exe remove";

if (args.Length == 0)
{
    return ModelWindow.Run(mainName, optionalName, publisher);
}

ModelWindow.AttachParentConsole();

if (args.Length == 1 && args[0] == "--help")
{
    Console.WriteLine(usage);
    return 0;
}
if (!(args.Length == 2 && args[0] == "install") && !(args.Length == 1 && args[0] == "remove"))
{
    Console.Error.WriteLine(usage);
    return 2;
}

var operation = "checking the installed packages";
try
{
    if (Package.Current.Id.Name != mainName || Package.Current.Id.Publisher != publisher)
    {
        throw new InvalidOperationException("Install Main.msix and run the registered optional-msix.exe command.");
    }

    var manager = new PackageManager();
    Package? FindOptional() => manager.FindPackagesForUserWithPackageTypes("", PackageTypes.Optional)
        .SingleOrDefault(package => package.Id.Name == optionalName && package.Id.Publisher == publisher);

    var installed = FindOptional();
    if (args[0] == "install")
    {
        operation = "resolving the local MSIX path";
        var path = Path.GetFullPath(args[1]);

        if (installed is not null)
        {
            Console.WriteLine("Updating optional content may close this app. Check package registration afterward.");
            Console.Out.Flush();
        }
        operation = "installing optional content";
        // Windows validates the package signature and certificate trust.
        await manager.AddPackageAsync(new Uri(path), null, DeploymentOptions.None);
        operation = "checking registration after installation";
        installed = FindOptional() ?? throw new InvalidOperationException("The optional package was not registered.");
        Console.WriteLine($"Optional content installed: {installed.Id.FullName}");
    }
    else if (installed is null)
    {
        Console.WriteLine("Optional content is already absent. Main app is unchanged.");
    }
    else
    {
        // SetInUseAsync(false) is not supported for a packaged desktop main app.
        Console.WriteLine("Removing optional content may close this app. Check package registration afterward.");
        Console.Out.Flush();
        operation = "removing optional content";
        await manager.RemovePackageAsync(installed.Id.FullName);
        operation = "checking registration after removal";
        if (FindOptional() is not null) { throw new InvalidOperationException("Optional content is still registered."); }
        Console.WriteLine("Optional content removed. Main app is unchanged.");
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Failed while {operation} (0x{error.HResult:X8}): {error}");
    return 1;
}

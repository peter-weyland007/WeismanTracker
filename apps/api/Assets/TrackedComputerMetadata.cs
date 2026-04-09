namespace api.Assets;

public static class TrackedComputerMetadata
{
    private static readonly string[] LaptopKeywords =
    [
        "laptop",
        "notebook",
        "ultrabook",
        "macbook",
        "thinkpad",
        "latitude",
        "elitebook",
        "probook",
        "zenbook",
        "travelmate",
        "surface laptop",
        "surface book"
    ];

    private static readonly string[] DesktopKeywords =
    [
        "desktop",
        "optiplex",
        "workstation",
        "tower",
        "sff",
        "small form factor",
        "all-in-one",
        "aio",
        "thinkcentre",
        "prodesk",
        "elitedesk",
        "mini pc",
        "nuc",
        "precision tower"
    ];

    private static readonly string[] ServerKeywords =
    [
        "server",
        "rack",
        "blade",
        "poweredge",
        "proliant"
    ];

    private static readonly string[] VirtualMachineKeywords =
    [
        "virtual machine",
        "vmware",
        "hyper-v",
        "virtualbox",
        "azure vm",
        "ec2"
    ];

    private static readonly string[] LaptopSignalKeywords =
    [
        "laptop",
        "notebook",
        "portable",
        "mobile workstation"
    ];

    private static readonly string[] DesktopSignalKeywords =
    [
        "desktop",
        "tower",
        "small form factor",
        "sff",
        "mini pc",
        "all-in-one",
        "aio",
        "workstation"
    ];

    private static readonly string[] ServerSignalKeywords =
    [
        "server",
        "rack",
        "blade"
    ];

    public static readonly IReadOnlyList<string> AllowedVariants = ["Unknown", "Laptop", "Desktop", "Server", "Virtual Machine", "Other"];

    public static string? NormalizeSerialNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToUpperInvariant();
    }

    public static string? NormalizeVariant(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim();
        return AllowedVariants.FirstOrDefault(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string InferVariant(params string?[] values)
    {
        var normalizedValues = values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim().ToLowerInvariant())
            .ToList();

        if (normalizedValues.Count == 0)
        {
            return "Unknown";
        }

        if (ContainsAny(normalizedValues, VirtualMachineKeywords))
        {
            return "Virtual Machine";
        }

        if (ContainsAny(normalizedValues, ServerKeywords))
        {
            return "Server";
        }

        if (ContainsAny(normalizedValues, LaptopKeywords))
        {
            return "Laptop";
        }

        if (ContainsAny(normalizedValues, DesktopKeywords))
        {
            return "Desktop";
        }

        return "Unknown";
    }

    public static string InferVariantFromStructuredSignals(string? chassisType, string? deviceType, params string?[] values)
    {
        var structuredSignals = new[] { chassisType, deviceType }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim().ToLowerInvariant())
            .ToList();

        if (structuredSignals.Count > 0)
        {
            if (ContainsAny(structuredSignals, VirtualMachineKeywords))
            {
                return "Virtual Machine";
            }

            if (ContainsAny(structuredSignals, ServerSignalKeywords))
            {
                return "Server";
            }

            if (ContainsAny(structuredSignals, LaptopSignalKeywords))
            {
                return "Laptop";
            }

            if (ContainsAny(structuredSignals, DesktopSignalKeywords))
            {
                return "Desktop";
            }
        }

        return InferVariant(values);
    }

    public static string ChooseVariantForSync(string? existingVariant, string? detectedVariant)
    {
        var normalizedExisting = NormalizeVariant(existingVariant);
        if (!string.IsNullOrWhiteSpace(normalizedExisting) && !normalizedExisting.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedExisting;
        }

        return NormalizeVariant(detectedVariant) ?? "Unknown";
    }

    private static bool ContainsAny(IEnumerable<string> values, IEnumerable<string> keywords)
        => values.Any(value => keywords.Any(keyword => value.Contains(keyword, StringComparison.Ordinal)));
}

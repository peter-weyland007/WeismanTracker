using api.Data;
using Microsoft.EntityFrameworkCore;

namespace api.Integrations;

public sealed class ReferenceMatchService : IReferenceMatchService
{
    private readonly AppDbContext _db;

    public ReferenceMatchService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<int?> MatchUserEntityIdAsync(GraphUserDto user, CancellationToken cancellationToken)
    {
        var candidateEmails = new[] { user.UserPrincipalName, user.Mail }
            .Select(NormalizeEmail)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (candidateEmails.Count == 0)
        {
            return null;
        }

        var people = await _db.TrackedPeople
            .AsNoTracking()
            .Where(x => x.DeletedAtUtc == null && x.Email != null)
            .Select(x => new { x.Id, x.Email })
            .ToListAsync(cancellationToken);

        // Strong match first: exact normalized email/UPN.
        var byEmail = people.FirstOrDefault(p => candidateEmails.Contains(NormalizeEmail(p.Email), StringComparer.Ordinal));
        if (byEmail is not null)
        {
            return byEmail.Id;
        }

        // Fallback: local-part match only when unique to avoid false positives.
        var candidateLocals = candidateEmails
            .Select(EmailLocalPart)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (candidateLocals.Count == 0)
        {
            return null;
        }

        var localMatches = people
            .Where(p => candidateLocals.Contains(EmailLocalPart(p.Email), StringComparer.Ordinal))
            .Select(p => p.Id)
            .Distinct()
            .ToList();

        return localMatches.Count == 1 ? localMatches[0] : null;
    }

    public Task<int?> MatchComputerEntityIdFromNinjaAsync(NinjaDeviceDto device, CancellationToken cancellationToken)
        => MatchComputerAsync(
            serialOrAssetCandidates: new[] { device.SerialNumber },
            hostnameCandidates: new[] { device.Hostname, device.DeviceName },
            machineIdCandidates: Array.Empty<string?>(),
            cancellationToken);

    public Task<int?> MatchComputerEntityIdFromGraphDeviceAsync(GraphDeviceDto device, CancellationToken cancellationToken)
        => MatchComputerAsync(
            serialOrAssetCandidates: new[] { device.SerialNumber },
            hostnameCandidates: new[] { device.DisplayName },
            machineIdCandidates: new[] { device.DeviceId },
            cancellationToken);

    public Task<int?> MatchComputerEntityIdFromIntuneAsync(IntuneManagedDeviceDto device, CancellationToken cancellationToken)
        => MatchComputerAsync(
            serialOrAssetCandidates: new[] { device.SerialNumber },
            hostnameCandidates: new[] { device.DeviceName },
            machineIdCandidates: new[] { device.AzureAdDeviceId },
            cancellationToken);

    public Task<int?> MatchComputerEntityIdFromAzureVmAsync(AzureVmDto vm, CancellationToken cancellationToken)
        => MatchComputerAsync(
            serialOrAssetCandidates: Array.Empty<string?>(),
            hostnameCandidates: new[] { vm.Name },
            machineIdCandidates: new[] { vm.VmId },
            cancellationToken);

    private async Task<int?> MatchComputerAsync(
        IEnumerable<string?> serialOrAssetCandidates,
        IEnumerable<string?> hostnameCandidates,
        IEnumerable<string?> machineIdCandidates,
        CancellationToken cancellationToken)
    {
        var serialCandidates = serialOrAssetCandidates
            .Select(NormalizeKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var machineIdAsAssetCandidates = machineIdCandidates
            .Select(NormalizeKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var hostCandidates = hostnameCandidates
            .SelectMany(ExpandHostnameCandidates)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (serialCandidates.Count == 0 && machineIdAsAssetCandidates.Count == 0 && hostCandidates.Count == 0)
        {
            return null;
        }

        var computers = await _db.TrackedComputers
            .AsNoTracking()
            .Where(x => x.DeletedAtUtc == null)
            .Select(x => new { x.Id, x.AssetTag, x.Hostname })
            .ToListAsync(cancellationToken);

        // Priority 1: serial/asset tag equivalence
        var bySerial = computers.FirstOrDefault(c => serialCandidates.Contains(NormalizeKey(c.AssetTag), StringComparer.Ordinal));
        if (bySerial is not null)
        {
            return bySerial.Id;
        }

        // Priority 2: machine IDs can be stored in asset tags in some orgs
        var byMachineId = computers.FirstOrDefault(c => machineIdAsAssetCandidates.Contains(NormalizeKey(c.AssetTag), StringComparer.Ordinal));
        if (byMachineId is not null)
        {
            return byMachineId.Id;
        }

        // Priority 3: hostname (full and shortname)
        var byHostname = computers.FirstOrDefault(c => ExpandHostnameCandidates(c.Hostname).Any(h => hostCandidates.Contains(h, StringComparer.Ordinal)));
        return byHostname?.Id;
    }

    private static string NormalizeEmail(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string EmailLocalPart(string? email)
    {
        var normalized = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var at = normalized.IndexOf('@');
        return at <= 0 ? normalized : normalized[..at];
    }

    private static string NormalizeKey(string? value)
        => new string((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static IEnumerable<string> ExpandHostnameCandidates(string? hostname)
    {
        var raw = (hostname ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        var normalizedFull = raw.ToLowerInvariant();
        yield return normalizedFull;

        var firstLabel = normalizedFull.Split('.', 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (!string.IsNullOrWhiteSpace(firstLabel) && !string.Equals(firstLabel, normalizedFull, StringComparison.Ordinal))
        {
            yield return firstLabel;
        }
    }
}

using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>Verwaltung, Reiter „Richtlinien“ (M7): öffentliche Links und Dateianfragen an oder aus, Passwortpflicht, Höchstdauer in Tagen.</summary>
public sealed class PoliciesViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public const int MaxDays = 3650;

    public SharingPolicies? Policies { get; private set; }

    /// <summary>Nach dem Speichern, damit die Oberfläche (Menüs, Freigabedialog) die neuen Regeln sofort kennt.</summary>
    public event Action<SharingPolicies>? Saved;

    public async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync(async () => Policies = await api.GetPoliciesAsync(ct));
        OnChanged();
    }

    public async Task<bool> SaveAsync(SharingPolicies policies, CancellationToken ct)
    {
        if (policies.LinkMaxDays is < 1 or > MaxDays || policies.RequestMaxDays is < 1 or > MaxDays)
        {
            return Fail(Texts.InvalidDays);
        }

        SharingPolicies? saved = null;
        var ok = await RunAsync(async () => saved = await api.UpdatePoliciesAsync(policies, ct));
        if (ok)
        {
            Policies = saved;
            Saved?.Invoke(saved!);
            Inform(Texts.Saved);
        }

        return ok;
    }
}

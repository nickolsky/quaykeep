using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;

namespace Quaykeep.Views;

/// <summary>The dropdown shows the address's source. The editable field uses the address itself.</summary>
internal sealed record ForwardTargetChoice(string Address, string Label)
{
    public override string ToString() => Address;

    public static ForwardTargetChoice From(ServerEntry server, ForwardAddress choice, bool includeName)
    {
        var source = L.Get(choice.Source switch
        {
            ForwardAddressSource.Ssh => "Fwd.TargetSsh",
            ForwardAddressSource.Interface => "Fwd.TargetInterface",
            ForwardAddressSource.PublicOutbound => "Fwd.TargetOutbound",
            _ => "Fwd.TargetLastKnown",
        });
        var label = $"{choice.Address} ({source})";
        return new ForwardTargetChoice(choice.Address, includeName ? $"{server.Name}  ·  {label}" : label);
    }
}

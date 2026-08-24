using System.Linq;
using dietsetup.Diet;
using dietsetup.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace dietsetup.Gui;

/// <summary>
/// The "Diet Setup" dialog: one button per registered, picker-visible profile; picking one is a
/// single atomic action, no confirm step. No dismiss-via-titlebar-X -- picking a profile is
/// mandatory, so nobody gets stuck unconfigured. Full context:
/// notes/dietsetup-patch-internals.md#diet-setup-dialog--guidialogdietsetupcs.
/// </summary>
public class GuiDialogDietSetup : GuiDialog
{
    public override string ToggleKeyCombinationCode => null!;
    public override bool PrefersUngrabbedMouse => true;

    public GuiDialogDietSetup(ICoreClientAPI capi) : base(capi)
    {
        ComposeGui();
    }

    private void ComposeGui()
    {
        DietProfile[] profiles = DietProfileRegistry.PickerProfiles.ToArray();

        const double buttonWidth = 260.0;
        const double buttonHeight = 34.0;
        const double buttonGap = 6.0;
        const double contentWidth = buttonWidth;

        const double topClearance = 35.0; // clears the title bar
        const double introHeight = 60.0; // room for the intro paragraph to wrap at contentWidth
        const double bottomPadding = 14.0;

        double totalContentHeight = topClearance + introHeight + profiles.Length * (buttonHeight + buttonGap) + bottomPadding;

        double y = topClearance;

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds bgBounds = ElementBounds.FixedSize(contentWidth, totalContentHeight)
            .WithFixedPadding(GuiStyle.ElementToDialogPadding);

        GuiComposer composer = capi.Gui.CreateCompo("dietsetupdialog", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("dietsetup:dialogtitle"), OnTitleBarClose)
            .BeginChildElements(bgBounds);

        composer.AddStaticText(Lang.Get("dietsetup:dialogintro"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0.0, y, contentWidth, introHeight));
        y += introHeight;

        foreach (DietProfile profile in profiles)
        {
            string id = profile.Id;
            composer.AddButton(Lang.Get(profile.NameLangCode), () => { Pick(id); return true; }, ElementBounds.Fixed(0.0, y, buttonWidth, buttonHeight), EnumButtonStyle.Normal, "btn-profile-" + id);
            y += buttonHeight + buttonGap;
        }

        composer.EndChildElements();
        SingleComposer = composer;
        SingleComposer.Compose();
    }

    private void Pick(string profileId)
    {
        capi.Logger.Notification("[dietsetup] Sending selection: {0}", profileId);
        capi.Network.GetChannel(DietSetupModSystem.ChannelName).SendPacket(new DietSelectionPacket { ProfileId = profileId });
        TryClose();
    }

    private void OnTitleBarClose()
    {
        // Intentionally not calling TryClose(): finishing requires picking a profile (see class remarks).
    }
}

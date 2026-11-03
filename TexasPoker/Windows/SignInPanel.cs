using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace TexasPoker.Windows;

/// <summary>What the main window shows until the dealer is signed in with an approved character.</summary>
public sealed class SignInPanel
{
    private static readonly Vector4 SignInColor = new(0.18f, 0.32f, 0.58f, 1f);

    private readonly Plugin plugin;

    public SignInPanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private AuthClient Auth => plugin.Auth;

    public void Draw()
    {
        ImGui.Spacing();
        ImGui.SetWindowFontScale(1.4f);
        ImGui.TextColored(DealerControls.Gold, "Sign in to deal");
        ImGui.SetWindowFontScale(1f);
        ImGui.TextWrapped("Eorzean Poker is for approved dealers. Sign in with XIVAuth, choosing the character " +
                          "you deal on. Only characters Elenor has authorized can sign in.");
        ImGui.Spacing();

        var playing = plugin.LocalCharacter;
        if (playing is null)
        {
            ImGui.TextColored(DealerControls.Muted, "Log in to your character first.");
            return;
        }

        ImGui.TextColored(DealerControls.Muted, $"Playing as {playing}");

        if (Auth.Message.Length > 0)
            ImGui.TextColored(DealerControls.Red, Auth.Message);

        switch (Auth.Step)
        {
            case AuthClient.SignInStep.Starting:
                ImGui.TextUnformatted("Starting the sign-in...");
                return;

            case AuthClient.SignInStep.WaitingForUser:
                DrawCode();
                return;
        }

        if (Auth.SignedIn && !Auth.CharacterMatches)
        {
            ImGui.TextWrapped($"You're signed in as {Auth.SignedInAs}, but playing {playing}. " +
                              "Switch to that character, or sign in again with this one.");
        }
        else if (Auth.SignedIn && !Auth.Confirmed)
        {
            ImGui.TextUnformatted(Auth.Checking ? "Checking your sign-in..." : "Your sign-in needs to be confirmed by the server.");
            if (!Auth.Checking && ImGui.Button("Check again"))
                Auth.CheckSoon();
        }

        ImGui.Spacing();
        if (DealerControls.ColoredButton("Sign in with XIVAuth", SignInColor, new Vector2(240, 40)))
            Auth.StartSignIn();

        if (Auth.SignedIn)
        {
            ImGui.SameLine();
            if (ImGui.Button("Sign out", new Vector2(100, 40)))
                Auth.SignOut();
        }
    }

    private void DrawCode()
    {
        ImGui.TextWrapped("Your browser should have opened XIVAuth. Sign in there, choose your character and, if asked, enter this code:");
        ImGui.Spacing();
        ImGui.SetWindowFontScale(2f);
        ImGui.TextColored(DealerControls.Gold, Auth.UserCode);
        ImGui.SetWindowFontScale(1f);
        ImGui.Spacing();

        if (ImGui.Button("Open XIVAuth again"))
            Auth.OpenSignInPage();
        ImGui.SameLine();
        if (ImGui.Button("Copy link"))
            ImGui.SetClipboardText(Auth.VerificationUrl);
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            Auth.CancelSignIn();

        ImGui.TextColored(DealerControls.Muted, "Waiting for you to approve on XIVAuth...");
    }
}

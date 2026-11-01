# Texas Hold'em Dealer (Dalamud plugin)

A no-limit Texas Hold'em **dealer tool** for FFXIV venues. Only the dealer runs the plugin:

- shuffles (cryptographic RNG) and deals, sending each player their hole cards by `/tell`
- posts blinds, moves the button, and tracks bets, min-raises, short all-ins and side pots
- evaluates hands at showdown and pays out pots, splitting them and giving odd chips to the right player
- optional rake: a percentage of each contested pot with a per-hand cap and "no flop, no drop", never taken from uncalled bets, tracked as house earnings
- keeps a gil ledger per player (buy-ins, rebuys, stacks, net) that survives reloads
- announces the action in party chat (or another channel), rate-limited so you don't spam, in wording you can edit
- reads players' actions ("call", "raise to 20k") from chat and picks them for the dealer to confirm
- optional live web table that players can watch in a browser

## Install

1. In game, open `/xlsettings` → **Experimental**.
2. Under *Custom Plugin Repositories*, paste this and click **+**, then **Save**:

   ```
   https://raw.githubusercontent.com/Kynyssael/TexasPokerPlugin/main/repo.json
   ```

3. `/xlplugins` → **All Plugins** → search *Texas Hold'em Dealer* → **Install**.
4. `/poker` opens the table.

## Releasing an update

1. Bump `<Version>` in `TexasPoker/TexasPoker.csproj` (e.g. `0.1.0.0` → `0.2.0.0`). Dealers only get the update when the version goes up.
2. Run `.\release.ps1`. It builds in Release, copies the package to `release/latest.zip` and updates `repo.json`.
3. Commit both files and push. Dalamud picks up the new version the next time dealers check for updates.

## Layout

| Project | What it is |
|---|---|
| `TexasPoker.Engine` | Pure poker rules (cards, deck, hand evaluator, table/betting). No Dalamud dependency. |
| `TexasPoker` | The Dalamud plugin: ImGui window, chat sending, persistence. |
| `TexasPoker.Tests` | xUnit tests for the engine. |
| (separate repo) | [TexasPokerServer](https://github.com/Kynyssael/TexasPokerServer): the optional live web table server. |

## Build

You need XIVLauncher with Dalamud installed (the SDK finds it in `%AppData%\XIVLauncher\addon\Hooks\dev`) and the .NET 10 SDK.

```
dotnet build
dotnet test
```

## Run a dev build in game

1. In game, `/xlsettings` → **Experimental** → add the full path to `TexasPoker\bin\Debug\TexasPoker.dll` under *Dev Plugin Locations*, and save.
2. `/xlplugins` → **Dev Tools** → enable *Texas Hold'em Dealer*.
3. `/poker` opens the table. `/poker deal` deals a hand; `/poker state` announces the table state.

Turn off **Send to chat** at the top of the window to try it out: every message is then printed only in your own chat log.

## Dealing a session

1. **Players** tab: set the buy-in, then type a name and press Enter, or use *Seat my target* / *Seat whole party*. Take their gil by trade.
2. **Settings** tab: blinds, announce channel, message delay.
3. **Table** tab: *Deal hand*. Pick each player's action (or let the plugin pick it from what they type in chat) and press *Confirm*. *Undo* fixes a mistake; Ctrl+click *Cancel hand* for a misdeal.
4. To cash a player out, click *Cash out* on the Players tab and trade them the amount shown.

## Live web table (optional)

Players can watch a dealer's table in a browser: seats, stacks, bets, pot and board, updating live. The table
link never shows hole cards (except cards shown at showdown). Each player also gets a private link that adds
their own two cards and best hand.

One server, which you host, serves every dealer who uses the plugin. The server address is built into the plugin:
dealers only tick a box. Each dealer gets a **permanent** table link like `https://poker.example.com/t/aB3xQ9`.

How tables stay separate: each copy of the plugin makes its own secret table key. The first update for a table id
claims it on the server, and from then on only that key can update it. The server stores only a hash of each key,
in `tables.json`, so claims survive restarts. There's no shared password to leak.

### 1. Point the plugin at your server

Set your server's address in `TexasPoker/WebPublisher.cs` and rebuild:

```csharp
public const string ServerUrl = "https://poker.example.com";
```

### 2. Run the server

The server lives in its own repo, [Kynyssael/TexasPokerServer](https://github.com/Kynyssael/TexasPokerServer).
Its README covers setting it up in a Proxmox container, HTTPS with Cloudflare Tunnel or Caddy, updates and backups.

### 3. Dealers, in the plugin

1. **Web** tab: tick *Show my table on the web*. The status line turns green once the server accepts the table.
2. *Post in /p* shares the table link. *Tell every player their private link* sends each player their own link by `/tell`.
3. Links can't be clicked in game chat, so they're short enough to type. Players can also copy them with a chat plugin, or you can post them on Discord.
4. If one player's private link leaks, Ctrl+click *New link* on their row. The table link never changes.
5. To keep the same table link on another PC or after a reinstall, use *Move my table to another PC*: copy the backup code and restore it there.

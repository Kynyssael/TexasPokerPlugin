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

Dealers install it from this repository's own plugin list, and Dalamud keeps it up to date by itself.

1. In game: `/xlsettings` → **Experimental** → under *Custom Plugin Repositories* paste
   `https://raw.githubusercontent.com/Kynyssael/TexasPokerPlugin/main/repo/pluginmaster.json`, tick **Enabled**,
   click **+**, then **Save**.
2. `/xlplugins` → **All Plugins** → search *Texas Hold'em Dealer* → **Install**.
3. `/poker` opens the plugin; sign in as below.

Updates arrive like any other plugin's (`/xlplugins` → **Update**). Settings, players and the table link are kept.

**Moving from the dev plugin (the zip):** first remove the old one: `/xlsettings` → **Experimental** → delete its
entry under *Dev Plugin Locations* → **Save**, and restart the game. Then install as above. Settings carry over.

### Releasing a new version

1. Raise `<Version>` in `TexasPoker/TexasPoker.csproj`, then `dotnet build TexasPoker.slnx -c Release`.
2. `powershell -File publish-repo.ps1` copies the build to `repo/latest.zip` and updates `repo/pluginmaster.json`.
3. Commit and push `repo/`. Dealers' Dalamud offers the update within a few hours (or at once with *Check for updates*).

## Signing in

The plugin is for approved dealers only. Until you sign in, `/poker` shows a sign-in screen instead of the table.

1. Log in to the character you deal on, then click **Sign in with XIVAuth**.
2. Your browser opens XIVAuth. Sign in there, choose that character and enter the code the plugin shows.
3. The plugin unlocks. A sign-in lasts 30 days.

Only characters with a licence can sign in:

- **Individual licence:** Elenor approves the character on the server.
- **Venue licence:** the venue's owner adds the character as one of its dealers.

A character without one gets "isn't approved to deal yet" and can sign in again once licensed. The plugin works
only while you're on the character you signed in with. If the server can't be reached, your last sign-in keeps
working for a day. If your sign-in ends mid-hand, you can still finish that hand.

**Venue owners** get a **Venue** tab, with a section per venue (open the one you want; with one venue it starts
open). It shows how many of the venue's dealer seats are used and lists its dealers.
Type a dealer as `First Last@World` (or use *Add my target*) to add them; they can sign in right away, even if they
never have before. Ctrl+click *Remove* to take a dealer off, which signs them out unless they have another licence.
Tables of a venue's dealers show the venue's name, logo and colours on the website. *Upload logo...* picks an image
(PNG, JPEG or WebP under 256 KB); it shows on the tables once Elenor approves it, and until then the owner can
withdraw it. Under **Colours**, the owner picks the felt and an accent colour (trim, pot, titles) and clicks *Save
colours*; open tables change straight away. *Use house colours* goes back to the Eorzean Poker green and gold.

Every venue has a **venue page**, e.g. `eorzeanpoker.app/v/primal-den`, listing its open tables so players can find
them; *Open* and *Copy* are next to it on the Venue tab. Dealers who aren't the owner see which venue they deal for,
and its page.

A dealer with more than one licence (their own and a venue's, or several venues) picks **Show my table as** on the
Web tab: *My own table* (no venue branding) or one of their venues.

A table shown under a venue is listed on the venue's page. For a private game, untick **List my table on the
venue page** on the Web tab: the table keeps the venue's name and logo, but only people with the link find it.

The server side (XIVAuth app, `texaspoker allow`, `texaspoker venue ...`) is described in
[TexasPokerServer](https://github.com/Kynyssael/TexasPokerServer#licences).

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

## Run your own build in game

1. In game, `/xlsettings` → **Experimental** → add the full path to `TexasPoker\bin\Debug\TexasPoker.dll` under *Dev Plugin Locations*, and save.
2. `/xlplugins` → **Dev Tools** → enable *Texas Hold'em Dealer*.
3. `/poker` opens the table. `/poker deal` deals a hand; `/poker state` announces the table state.

Turn off **Send to chat** at the top of the window to try it out: every message is then printed only in your own chat log.

## Dealing a session

1. **Players** tab: set the buy-in, then type a name and press Enter, or use *Seat my target* / *Seat whole party*. Take their gil by trade.
2. **Settings** tab, in sections you open as needed: blinds and buy-in, rake, bomb pots and run it twice, chat and
   announcements, players' actions, and this window.
3. **Table** tab: *Deal hand*. Pick each player's action (or let the plugin pick it from what they type in chat) and press *Confirm*. *Undo* fixes a mistake; Ctrl+click *Cancel hand* for a misdeal.
4. To cash a player out, click *Cash out* on the Players tab and trade them the amount shown.
5. At the end of a night, Ctrl+click *Reset* next to *Hands dealt* on the Players tab: the next hand is Hand #1 again.

### Bomb pots and running it twice

Both are off until you turn them on in the **Settings** tab, and any dealer can use them.

- **Bomb pots:** tick *Bomb pots* and set the amount (0 = two big blinds). A *Bomb pot* box then sits above *Deal*:
  tick it and the next hand is a bomb pot. Everyone puts in that amount (or all they have), there are no blinds, and
  the hand starts on the flop. The box clears itself after that hand.
- **Run it twice:** tick *Run it twice when everyone is all-in*. When nobody can bet any more and cards are still to
  come, the rest of the board is dealt twice, and each pot is split between the two boards (the first gets the odd
  chip). The website shows both boards. Hands run twice don't count for venue promotions.

### Venue promotions (venue licence)

A venue's owner can run a **high hand bonus** and a **bad beat jackpot**, shared by all of the venue's tables, in the
**Promotions** part of the Venue tab. Each is off until the owner turns it on and clicks *Save promotions*.

- **High hand:** the best hand shown at showdown, using both hole cards, since the owner started the round (Ctrl+click
  *New round*, e.g. each event night) wins the owner's prize. The owner picks the minimum hand. The plugin announces
  each new high hand in chat; the venue pays the prize.
- **Bad beat jackpot:** each hand that sees a flop puts a cut into the jackpot (a percentage of the pot, with an
  optional cap, either on top of the rake or out of it). When a big enough hand (the owner picks: Aces full, any
  quads, Eights or better, Jacks or better, a straight flush), using both hole cards, loses the main pot at showdown
  with enough players dealt in, the plugin announces it and **pays the jackpot into the players' stacks**: by default
  50% to the loser, 25% to the winner and 25% shared by the rest of the table. 10% stays to start the next jackpot.
  The owner can also set the jackpot to an amount (Ctrl+click *Set jackpot*), e.g. to start it.

Dealers see the jackpot and the current high hand on the Table tab, and players see them on the table page and the
venue page. The jackpot and the high hand are kept on the server, so every table of the venue shares them.

## Live web table (optional)

Players can watch a dealer's table in a browser: seats, stacks, bets, pot and board, updating live. The table
link never shows hole cards (except cards shown at showdown). Each player also gets a private link
(`…/t/aB3xQ9#k7Hq2mXz9PwR`) that adds their own two cards and best hand.

One server, which you host, serves every dealer who uses the plugin. The server address is built into the plugin:
dealers only tick a box. Each dealer gets a **permanent** table link like `https://poker.example.com/t/aB3xQ9`,
owned by their signed-in character.

**Cards are end-to-end encrypted.** The part of a private link after `#` never leaves the player's browser. The
plugin encrypts each player's cards with a key made from it (see `CardSealer`), so the server, Cloudflare and their
logs only ever see ciphertext. A player's link ends when they cash out, and *New links for everyone* (Ctrl+click)
replaces all of them, e.g. at the start of an event. On their private page, players can tick *Hide my cards* so
their cards stay face-down unless they press and hold.

**Players can act from their private page.** On their turn the page shows Fold / Check / Call / Raise / All-in. Each
action is signed with the player's link, so neither the server nor anyone else can send one for them. The plugin
only applies it if it's for the current hand and that exact turn. By default it applies at once (Web tab: *Apply web
actions automatically*); turned off, it's picked for you to confirm, like an action typed in chat. Players can still
type in chat as before. Actions reach the plugin the moment they're sent, over a connection it keeps open to the
server; if that connection is down, it checks for them every 1.5 s until it's back.

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
   *Table name* sets the name shown on the table page and on your venue's page (empty = "Texas Hold'em").
   Unticking the box takes the table off the website at once (its link shows "waiting" until you tick it again).
2. *Post in /p* shares the table link. *Tell every player their private link* sends each player their own link by `/tell`.
3. Links can't be clicked in game chat, so they're short enough to type. Players can also copy them with a chat plugin, or you can post them on Discord.
4. If one player's private link leaks, Ctrl+click *New link* on their row. The table link never changes.
   Private links made before 0.2.1 (`?p=…`) no longer show cards: tell players their new links once.
5. To keep the same table link on another PC or after a reinstall, use *Move my table to another PC*: copy the backup code and restore it there.

# Eorzean Poker (Dalamud plugin)

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
2. `/xlplugins` → **All Plugins** → search *Eorzean Poker* → **Install**.
3. `/poker` opens the plugin; sign in as below.

Updates arrive like any other plugin's (`/xlplugins` → **Update**). Settings, players and the table link are kept.

**Moving from the dev plugin (the zip):** first remove the old one: `/xlsettings` → **Experimental** → delete its
entry under *Dev Plugin Locations* → **Save**, and restart the game. Then install as above. Settings carry over.

### Releasing a new version

1. Raise `<Version>` in `TexasPoker/TexasPoker.csproj`, then `dotnet build TexasPoker.slnx -c Release`.
2. `powershell -File publish-repo.ps1` copies the build to `repo/latest.zip` and updates `repo/pluginmaster.json`.
3. Commit and push `repo/`. Dealers' Dalamud offers the update within a few hours (or at once with *Check for updates*).

## Signing in

The first time you open it, and again whenever they change, `/poker` shows the terms of use: tick *I have read and
agree* and click **Accept and continue**. The sign-in comes after.

The plugin is for approved dealers only. Until you sign in, `/poker` shows a sign-in screen instead of the table.

1. Log in to the character you deal on, then click **Sign in with XIVAuth**.
2. Your browser opens XIVAuth. Sign in there, choose that character and enter the code the plugin shows.
3. The plugin unlocks. A sign-in lasts 30 days. Its key is saved encrypted for your Windows account, so a copy of
   your plugin settings is no use on another PC.

Only characters with a license can sign in:

- **Individual license:** Elenor approves the character on the server. Ask on Discord: `elenoramdali`.
- **Venue license:** the venue's owner adds the character as one of its dealers.

A character without one gets "isn't approved to deal yet" and can sign in again once licensed. The plugin works
only while you're on the character you signed in with. If the server can't be reached, your last sign-in keeps
working for a day. If your sign-in ends mid-hand, you can still finish that hand.

**Venue owners** get a **Venue** tab, with a section per venue (open the one you want; with one venue it starts
open). It shows how many of the venue's dealer seats are used and lists its dealers.
Type a dealer as `First Last@World` (or use *Add my target*) to add them; they can sign in right away, even if they
never have before. Ctrl+click *Remove* to take a dealer off, which signs them out unless they have another license.
Tables of a venue's dealers show the venue's name, logo and colours on the website. *Upload logo...* picks an image
(PNG, JPEG or WebP under 256 KB); it shows on the tables once Elenor approves it, and until then the owner can
withdraw it. Under **Colours**, the owner picks the felt and an accent colour (trim, pot, titles) and clicks *Save
colours*; open tables change straight away. *Use house colours* goes back to the Eorzean Poker green and gold.
Under **Cards**, the owner picks the card face (cream, white or parchment), corners (classic or round), font
(standard, serif or typewriter) and back: stripes in two colours, a plain colour, the venue logo, or their own
picture, which shows once Elenor approves it (like the logo). *Save cards* applies it to open tables.

Every venue has a **venue page**, e.g. `eorzeanpoker.app/v/primal-den`, listing its open tables so players can find
them; *Open* and *Copy* are next to it on the Venue tab. Dealers who aren't the owner see which venue they deal for,
and its page.

A dealer with more than one license (their own and a venue's, or several venues) picks **Show my table as** on the
Web tab: *My own table* (no venue branding) or one of their venues.

A table shown under a venue is listed on the venue's page. For a private game, untick **List my table on the
venue page** on the Web tab: the table keeps the venue's name and logo, but only people with the link find it.

The server side (XIVAuth app, `texaspoker allow`, `texaspoker venue ...`) is described in
[TexasPokerServer](https://github.com/Kynyssael/TexasPokerServer#licenses).

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
2. `/xlplugins` → **Dev Tools** → enable *Eorzean Poker*.
3. `/poker` opens the table. `/poker deal` deals a hand; `/poker state` announces the table state.

Turn off **Send to chat** at the top of the window to try it out: every message is then printed only in your own chat log.

## Dealing a session

1. **Players** tab: set the buy-in, then type a name and press Enter, or use *Seat my target* / *Seat whole party*. Take their gil by trade.
   A table seats up to 7 players.
2. **Settings** tab, in sections you open as needed: blinds and buy-in, rake, bomb pots and run it twice, chat and
   announcements, players' actions, and this window.
3. **Table** tab: *Deal hand*. Pick each player's action (or let the plugin pick it from what they type in chat) and press *Confirm*. *Undo* fixes a mistake; Ctrl+click *Cancel hand* for a misdeal.
   Right-click a seat (on the table or in the list) for *Rebuy*, *Adjust*, *Sit out* and *Cash out*, the same as on the
   Players tab. Between hands, *Seat my target*, *Seat me* and *Seat whole party* sit under the table, and waiting
   trades show at the top.
   *Call turn*, next to the player to act (or `/poker call`), says whose turn it is again in chat with a game sound,
   for a busy venue. Pick the sound under Settings → Chat and announcements (*Hear it* plays it to you only).
   **Turn timer** (off until you tick it under Settings → Players' actions, 15–300 s, 60 by default): at half the
   time the plugin calls the player's turn, and when it runs out it checks for them if they can, otherwise folds
   ("Time's up, Name: fold."). The countdown sits next to the player to act, with *Pause* (for a ruling) and *More
   time* (their full time again); players see it on the web table. While you have an action picked, the time
   waits for you to confirm it.
4. To cash a player out, click *Cash out* on the Players tab, then *Remove them and trade*: the plugin asks them to
   trade and types in their gil once they accept. Check it (they may put gil in too) and press *Trade* and *Confirm*.
   Untick *Open a trade with them* in that popup (or *Open a trade when cashing out* in Settings) to trade them
   yourself. Anything still owed shows under **Gil to pay** on the Players tab, with a *Trade* button each.
5. At the end of a night, Ctrl+click *Reset* next to *Hands dealt* on the Players tab: the next hand is Hand #1 again,
   and a new session log starts.

**Trades.** The plugin notices when a trade moves gil (it reads only your own gil and who you traded with). A trade
for something you already entered, like a buy-in, rebuy or cash-out, is just checked off: "their buy-in is collected", or "300,000
still to pay" (a trade carries at most 1,000,000 gil, so big amounts come in parts). Any other gil from a seated player goes
straight onto their stack as a rebuy (during their hand, as soon as the hand is over), with *Undo* if it was a tip or a
payback; untick *Add traded gil to the player's stack* (Settings, blinds and buy-in) to add it yourself instead. Gil
from someone not seated shows at the top of the Players and Table tabs: *Seat with* that amount, or *Ignore*. Paying more than the table said, or paying
someone nothing is owed to, is flagged in red. Every trade goes in the session log. Trades are only watched while the
poker window or the dealer bar is open, so your own trades away from the table stay out of it.

**Playing at your own table.** With only one player around, click *Seat me* on the Players tab (or seat yourself as
your target). Your buy-in is your own gil, and party chat is told the dealer is playing. While you're seated, the
plugin keeps everyone else's cards from you: *Show all hole cards* is off, your copy of each card and link tell is
hidden from your chat (your own cards print in your chat only), and the Web tab hides the other players' links and
PINs. You act like any player, from the Table tab or by typing in chat. It can't stop a dealer who goes out of their
way to cheat (another chat logging plugin still sees the tells), so players are trusting you, and the chat notice
says so.

**Session log, for your records.** The **Log** tab keeps the whole session, each line with the time: everything the
table announced, plus the gil you took and paid (buy-ins, rebuys, adjustments, cash-outs with each player's net, rake
and jackpot payouts, bonuses), and each action a player typed in chat or a tell word for word with how it was read
(`Name@World (/p): "call" -> read as Call 100`), or sent from their web page. It's written to a text file as it goes (`pluginConfigs/TexasPoker/Session logs`, one
file per session), so a reload or a crash loses nothing: the plugin carries on with the same file. *Export...* saves a
copy anywhere, *Copy all* puts it on the clipboard, *Open folder* shows every session's file, and Ctrl+click *New
session* starts a new one. The tab shows the last 500 lines.

### Bomb pots and running it twice

Both are off until you turn them on in the **Settings** tab, and any dealer can use them.

- **Bomb pots:** tick *Bomb pots* and set the amount (0 = two big blinds). A *Bomb pot* box then sits above *Deal*:
  tick it and the next hand is a bomb pot. Everyone puts in that amount (or all they have), there are no blinds, and
  the hand starts on the flop. The box clears itself after that hand.
- **Run it twice:** tick *Run it twice when everyone is all-in*. When nobody can bet any more and cards are still to
  come, the rest of the board is dealt twice, and each pot is split between the two boards (the first gets the odd
  chip). The website shows both boards. Hands run twice don't count for venue promotions.

### Venue promotions (venue license)

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
  With the jackpot on, the dealer list has a *Bad beat jackpot* box per dealer, the owner included. Untick it and
  that dealer's table takes no cut, can't hit the jackpot and doesn't show it, from its next hand.
  Each dealer's Players tab shows **Bad beat jackpot cuts**: what their table put in, still in their gil, counted in
  *Gil you should have on you*. A jackpot paid at their table comes off it (below 0, the venue owes them). *Hand
  over* clears it once they've given the gil to the owner, or *Settled* once the venue has paid them back.

Dealers see the jackpot and the current high hand on the Table tab, and players see them on the table page and the
venue page. The jackpot and the high hand are kept on the server, so every table of the venue shares them.

### Tournaments (Tournament License)

A venue with the **Tournament License** (an add-on to its venue license; ask Elenor on Discord: `elenoramdali`) gets a
**Tournaments** part on its owner's Venue tab. A tournament runs up to 8 tables of 7 players (56 entries), freezeout:
one entry each, no rebuys, no late entry.

1. **New tournament:** a name, the entry fee, the prize pool (the entry fees, or an amount you set), starting chips,
   the blind structure, the prize split (percent for 1st, 2nd, 3rd...) and points by place. *Use this structure*
   fills in a ready-made one: **Turbo** (8 min levels), **Regular** (12 min) or **Deep** (15 min, 20,000 chips),
   with antes from level 4 and breaks; every level, ante and break can be changed.
2. **Register** the players at the start of the event, once each has traded you the entry fee (type them as
   `First Last@World`, or *Register my target*). Their fee trade is checked off.
3. Pick a **dealer for each table** (one table per 7 players: you or the venue's dealers, each a different one;
   dealers don't play) and Ctrl+click **Start**. Registration closes and the server draws the players to tables as
   evenly as possible (17 players on 3 tables: 6, 6, 5), with a random button at each.
4. Each dealer, with the web table on, sees their players at the top of their Table tab and clicks **Seat the
   players** (the table must be empty). The clock starts once every table is seated (or Ctrl+click *Start the
   clock* to start it anyway).
5. The server keeps the clock: the Table tab shows the level, blinds, ante and time left, each new level starts with
   the next hand, and chat hears "Blinds go up to 200/400 in 1 minute". Breaks stop the dealing. The owner can pause
   and resume the clock for everyone.
6. Stacks are chips, not gil: no buy-ins, rebuys or cash-outs, traded gil doesn't go on stacks, and there's no rake,
   jackpot cut or bomb pot. A player marked *Away* is still dealt in, posts their blinds and antes, and is folded on
   their turn.
7. After each hand the plugin reports the stacks; a player with no chips left is out, and chat says their place
   ("Aya Ren is out in 5th place."). Players out in the same hand: the one who started it with more chips finishes
   higher, and the same stack shares the place (and its prize and points).
8. **Moving players.** Tables are kept within one player of each other. When one has two more than another, its dealer
   sees "Move Aya Ren to table 3 (Dealer)" and clicks *Send Aya Ren* between hands (the plugin won't deal until they
   do): she's removed with her chips and told by /tell where to go. The player who moves is the one who'd be big
   blind next, so nobody skips the blinds or pays them twice. Table 3's dealer sees "Aya Ren is coming from table 1
   with 23,450 chips" and clicks *Seat Aya Ren* once she's there: she sits just before the button and is dealt in.
9. **Breaking tables.** When the players left fit on one table fewer, the table with the fewest players breaks: its
   players are sent to the others the same way, and its dealer's plugin says the table is closed. When 7 or fewer
   are left, everyone ends up at one **final table**.
10. **Hand-for-hand.** When the next player out would be the last one with no prize (the bubble), every table deals
    one hand and then waits for the others ("Hand-for-hand: waiting for the other tables"). Players out in the same
    round count as out at the same time, ranked by the chips they started their hand with. It stops once the rest are
    in the money. The owner can also set it *On now* or *Off*.
11. **If something goes wrong.** Between hands, the Table tab warns when its players or chips differ from the server's
    after the same hand (a crash, a reload), with Ctrl+click *Use the server's stacks*. If a dealer can't come back,
    the owner picks another dealer for that table and Ctrl+clicks *Give table*: the new dealer seats its players with
    the chips from its last hand, and the old dealer's plugin lets the table go.
12. When one player has all the chips, chat names the winner and the prizes, and the table is a cash table again. The
   owner's Venue tab shows the results: place, prize, points, with a **Trade** button for each prize and a *Paid*
   box. Ctrl+click *Cancel tournament* ends it early (refund the entry fees).

While it runs, the owner sees each table with its dealer and players, the moves in progress, and every player's
table and chips. The table page shows the tournament above the table: its name, the table number, level, blinds and
ante, the time to the next level, how many players are left, and hand-for-hand.

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
   On a phone, the long private link is easy to mistype, so each tell also has a **seat PIN**: the player opens the
   table link, taps their seat and enters the 4 digits. It works once, for 10 minutes, and 5 wrong tries for a seat
   stop it: the dealer gets a notice in chat within a minute (someone may be guessing) and can tell them again for a new one.
   Entering a PIN never kicks out a player who already has their cards open. The phone keeps the link, so they won't need another PIN when they come back.
   The link goes to the server locked with the PIN, only while the PIN works, and the server hands it over only for the right PIN.
4. If one player's private link leaks, Ctrl+click *New link* on their row. The table link never changes.
   Private links made before 0.2.1 (`?p=…`) no longer show cards: tell players their new links once.
5. To keep the same table link on another PC or after a reinstall, use *Move my table to another PC*: copy the backup code and restore it there.

## License

The code is public so anyone can read it and check what the plugin does, but it isn't open source. You may read
it and build it unchanged to compare with the release. Changing it, getting around the sign-in or the terms of use,
or sharing it or a build of it needs Elenor's permission. See [LICENSE](LICENSE).

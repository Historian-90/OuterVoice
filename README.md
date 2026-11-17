# Outer Voice

A Dalamud plugin that gives your FFXIV character a voice. Record lines for things that happen
in duties (raises, deaths, Kardia swaps, big heals, wipes, clears) and they play, with the text
shown as a battle-talk pop-up, chat line or toast. Share your voice pack with friends: when
you're in a party together, you hear their character too, and they hear yours.

Everything runs on your own machine. The plugin only reads what your game already shows you
(HP bars, status icons, cast bars, duty state). Nothing is sent anywhere while you play.

## Install

1. In game, open `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, paste this link, tick **Enabled**, and click **Save**:
   ```
   https://raw.githubusercontent.com/Historian-90/OuterVoice/main/repo.json
   ```
3. Open `/xlplugins`, search for **Outer Voice**, and install it.
4. Type `/ov` (or `/outervoice`) to open it.

## Make your voice pack

1. **Voice Pack** tab: click **Browse...** and pick a folder (an empty one is fine), then
   **Create a new voice pack here**. Click **Use my character** so friends' plugins know whose
   voice it is.
2. **Lines** tab: add the jobs you play, then write lines under each event. Click
   **Add audio...** on a line to attach a recording (mp3 or wav); it's copied into your pack folder.
3. Click **Save** at the top when you're done.
4. **Test** tab: play any event to hear it.

Lines in the **ALL** group play on any job. Each line has a weight (how often it's picked) and
a mood note for whoever records it.

## Share with friends

- **Voice Pack** tab → **Export pack for friends...** makes one .zip (clips are compressed for
  sharing by default). Send it however you like.
- **Friends** tab → **Import a pack...** to add a friend's .zip. Each friend has their own mute
  and volume.

Friends' voices play when they're in your party.

### Sync through Dropbox or OneDrive (no more passing zips around)

- **Share yours:** Voice Pack tab -> **Share through Dropbox or OneDrive** -> pick a folder inside your
  Dropbox or OneDrive folder. Your pack's zip is kept there and re-published every time you save.
  Share **that file** ("Anyone with the link" can view -> Copy link) and send the link to friends once.
- **Follow a friend:** Friends tab -> paste their link -> **Subscribe**. The plugin checks it when you
  log in and every 30 minutes, and downloads only when it changed. Per friend, choose **Auto** updates
  or ask first (an **Update** button appears).

Supported links: Dropbox file links, and personal OneDrive file links (1drv.ms / onedrive.live.com).

Safety: only https links on Dropbox's or OneDrive's own servers are accepted (checked on every
redirect too), downloads must be a real pack zip under 200 MB that unpacks to under 400 MB, line
text is stripped of hidden control codes, friends' clips over 30 seconds are skipped, **Tame loud
clips** limits their loudness, and a link can never switch to voicing a different character than
the one it first delivered.
Privacy: anyone with your link can download your pack, and the share page may show your account
name to people who open it in a browser. You can turn the link off at any time.

### No audio chaos

- **One voice at a time.** All voices share one audio channel and one gap between lines. Only a
  more urgent line can cut in, and it replaces what's playing instead of overlapping.
- **Personal events belong to their owner.** A raise, death, Kardia swap or big heal is only
  voiced by the character who did it.
- **No repeats per trigger.** The line a character last said for an event won't be picked for that
  event again until a different line for the same event has played. If an event only has one line,
  it can play again once the character has said something else in between.
- **Quiet during scenes.** During cutscenes, NPC dialogue and loading screens (and for a few
  seconds after), no voice or text plays, anything playing stops, and nothing is saved up to play
  afterwards.
- **Shared events get one speaker.** Duty start, wipes, clears, tank deaths, clutch saves and big
  pulls are voiced by one party member, picked the same way on everyone's machine (with the same
  line), so everyone hears the same thing.

## Events

**Every job**

| Event | When it fires |
|---|---|
| Duty start | A duty commences |
| Clutch save | Someone drops under 15% HP and is back above 50% within 3 seconds |
| Tank death | A tank in the party dies |
| Raise | Someone gets a Raise from you |
| You die | You die |
| Wipe | The party wipes |
| Big pull | 5+ targetable, hostile enemies in combat within 30 yalms for over a second |
| Duty complete / Clean clear | Duty cleared (clean = no deaths) |
| Limit Break 1 / 2 / 3 | You start casting a Limit Break (works without a gauge too, e.g. Explorer Mode) |

**Social** (every job)

| Event | When it fires |
|---|---|
| Joining a party | You join someone else's party (normal or cross-world) |

Only the person who joins speaks: you when you join someone's party, and a friend with a pack (in
their voice) when they join yours. Forming a party by inviting someone doesn't play your line, and
Duty Finder groups don't count.

**Treasure** (every job)

| Event | When it fires |
|---|---|
| Treasure coffer | You open a treasure coffer (dungeons, raids, and anywhere else) |
| Treasure map coffer | You open a treasure map coffer |

Only the person who opens the coffer speaks (you, or a friend with a pack when they open one).
Coffers other people open stay silent, and so do coffers that are already open when you arrive.

**Weather** (every job)

The weather changes while you're out in the world, or you arrive somewhere with inclement weather
(anything but clear skies and clouds). Arriving doesn't repeat: teleporting from rain to more rain
is silent, and each kind of weather plays at most once per 30 minutes on arrival. Nothing is said in
duties or combat. Everyone in the zone sees the same weather, so one voice in the
party comments.

| Event | Weather |
|---|---|
| Clear skies | Clear Skies, Fair Skies |
| Clouds | Clouds |
| Fog | Fog |
| Wind | Wind, Gales |
| Rain | Rain, Showers |
| Thunder | Thunder, Thunderstorms |
| Snow | Snow, Blizzards |
| Heat | Hot Spells, Heat Waves |
| Dust | Dust Storms, Sandstorms |
| Gloom | Gloom, umbral weather and anything stranger (Auroras, Moon Dust...) |

**Idle** (every job)

Random chatter while you're standing around. It only starts after you've stood still for a while:
moving, fighting, a duty, crafting or gathering starts the wait over (turning on the spot and emotes
don't). On the Settings tab, choose how often (Off, Rare, Normal, Frequent) and how long you must
stand still first (default 5 minutes).
Friends who are idle too hear the same line from the same one voice.

**Job-specific**

| Job | Events |
|---|---|
| PLD, WAR, DRK, GNB | Tank stance on, Invulnerability (Hallowed Ground, Holmgang, Living Dead, Superbolide), Burst window |
| WHM | Big heal (Temperance, Liturgy of the Bell), Burst (Presence of Mind) |
| SCH | Big heal (Seraphism), Burst (Dissipation) |
| AST | Big heal (Neutral Sect, Macrocosmos), Burst (Divination), Card play |
| SGE | Big heal (Pneuma, Kerachole, Holos, Panhaima), Kardia swap |
| DNC | Burst (Technical Finish, Devilment), Dance partner swap |
| BRD | Burst (Battle Voice, Radiant Finale), New song |
| RPR | Burst (Arcane Circle), Enshroud |
| MNK, DRG, NIN, SAM, VPR, MCH, BLM, SMN, RDM, PCT | Burst window (each job's own big buff) |

Job abilities live on each job's page, not on ALL. The Lines tab only shows the events that apply to the job you're editing; hover the (i) next to an
event to see exactly which buffs trigger it. For **Kardia** and **Dance partner**, tick a line's
**Initial** box to make it play when you first choose a partner; unticked lines play when you switch. A stance or buff that's
already on when you zone in doesn't trigger a line.

Skill names are looked up from the game in English, so this currently assumes an English client.

## Custom events

On the **Custom Events** tab you can make your own triggers. A custom event plays a line the moment
**all** of its conditions are true at the same time. Conditions can be:

- **Casting** an action (anything with a cast bar)
- **Has status**: a game buff or debuff on you
- **Doing emote**: e.g. `wave` or `/wave`
- **Has Moodle**: a custom status from the Moodles plugin (needs Moodles; friends' Moodles count
  when they're synced to you)

Write the lines for a custom event on the Lines tab (the ALL group works for every job). Custom
events are saved in your voice pack, so friends who import it hear them too.

Give events a **Category** (Emotes, RP, Moodles...) to group them, switch them **On/Off** without
deleting them, and use **Edit lines ->** / **Edit trigger** to jump between an event and its lines.

## Staying organized

The Lines tab is split into **Every job** (Combat & duties, Social, Treasure, Weather, Idle), **Job abilities** (on a
job's page) and **Custom events** (by category),
and remembers which sections you've collapsed. Use the search box to find events or line text, and
**Hide empty** to show only events that already have lines (add lines to the others with
**+ Add lines to...**).

## Commands

`/ov` is short for `/outervoice`. (The game already uses `/voice` to mute voices.)

- `/ov`: open the window
- `/ov on` / `/ov off`
- `/ov test <event> [job]`, e.g. `/ov test raise SGE`
- `/ov reload`: re-read your pack after editing files by hand

## Lip sync

While a line plays, the speaking character's mouth moves using the game's own talking animation
(the one NPCs use in dialogue). It only affects the mouth, so whatever the body is doing keeps
going. It's visual and local: friends who have your pack see your character talk on their screen.
Turn it off on the Settings tab if you prefer.

## Volume

Voices play through Windows directly rather than through the game's sound channels. By default
they follow the game's **Master** and **Voice** volume (and go silent when either is muted); turn
that off on the Settings tab if you'd rather control them separately. The plugin's own Volume
slider and per-friend volumes apply on top.

## Building it yourself

Requires the .NET 10 SDK, and the game must have been run with Dalamud once.

```
dotnet build
```

Then add the built `OuterVoice.dll` under **Dev Plugin Locations** in `/xlsettings` → Experimental.

## Releasing (maintainer)

Raise `<Version>` in `OuterVoice/OuterVoice.csproj` (e.g. `0.1.1.0`) and push to `main`.
GitHub Actions builds the plugin, publishes a release (`v0.1.1`), and updates `repo.json` so
everyone gets the update. You can also run the **Release** workflow by hand from the Actions tab.

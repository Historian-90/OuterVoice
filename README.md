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

## Finding your way around

The icons on the left switch pages: **Lines** (what your character says), **Voice Pack** (folder,
character, sharing), **Friends** (friends' packs) and **Settings**.

## Make your voice pack

1. **Voice Pack** page: click **Browse...** and pick a folder (an empty one is fine), then
   **Create a new voice pack here**. Click **Use my character** so friends' plugins know whose
   voice it is.
2. **Lines** page: pick a job at the top left (**+** adds the jobs you play). Click **+ Add event**,
   pick an event (a wipe, arriving in Kugane, rain...) and type a line on the right. Click
   **Add audio...** on a line to attach a recording (mp3 or wav); it's copied into your pack folder.
3. Click **Save** at the top when you're done.
4. **Play** (top right of an event) plays one of its lines. Settings > Testing plays any event.

Lines under **Every job** play on any job. Each line has a weight (how often it's picked) and
a mood note for whoever records it.

## Portrait

On the **Voice Pack** page, **Portrait > Choose a picture...** sets your character's picture (PNG or
JPG). With **Settings > Text pop-ups > Show text as: Battle-talk pop-up**, it appears in the game's
own pop-up, like an NPC's. **Size**, **Left / right** and **Top / bottom** frame it (each change
shows a preview pop-up). A PNG with a transparent background looks most like the game's NPC
portraits. The picture and its framing travel with your pack, so friends see it in their pop-ups.

**Settings > Text pop-ups > Box style** picks the pop-up's look: **Parchment** (the usual one),
**Frosted**, **Shadow** (see-through black) or **Classic blue**. Picking one shows a preview.

This needs **Penumbra**: Outer Voice asks Penumbra to show your picture in place of a spare game
icon while the plugin is running (nothing is installed, and it's gone when the plugin unloads).
Without Penumbra, pop-ups show without a picture.

## Share with friends

- **Voice Pack** page → **Export pack for friends...** makes one .zip (clips are compressed for
  sharing by default). Send it however you like.
- **Friends** page → **+ Add friend** → **Import a .zip file instead...** to add a friend's .zip.
  Pick a friend in the list for their mute and volume.

Friends' voices play when they're in your party.

### Sync through Dropbox or OneDrive (no more passing zips around)

- **Share yours:** Voice Pack page -> **Share through Dropbox or OneDrive** -> pick a folder inside your
  Dropbox or OneDrive folder. Your pack's zip is kept there and re-published every time you save.
  Share **that file** ("Anyone with the link" can view -> Copy link) and send the link to friends once.
- **Follow a friend:** Friends page -> **+ Add friend** -> paste their link -> **Subscribe**. The
  plugin checks it when you log in and every 30 minutes, and downloads only when it changed. Pick the
  friend in the list to choose automatic updates or ask first (an **Update** button appears).

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

**General events**

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

**Social** (general events)

| Event | When it fires |
|---|---|
| Joining a party | You join someone else's party (normal or cross-world) |

Only the person who joins speaks: you when you join someone's party, and a friend with a pack (in
their voice) when they join yours. Forming a party by inviting someone doesn't play your line, and
Duty Finder groups don't count.

**Treasure** (general events)

| Event | When it fires |
|---|---|
| Treasure coffer | You open a treasure coffer (dungeons, raids, and anywhere else) |
| Treasure map coffer | You open a treasure map coffer |

Only the person who opens the coffer speaks (you, or a friend with a pack when they open one).
Coffers other people open stay silent, and so do coffers that are already open when you arrive.

**Places** (general events)

You arrive in a different kind of place: City, Field, Inn, Residential area, Home, House,
Apartment, Gold Saucer or Island Sanctuary.

- **Cities** have their own lines too: Limsa Lominsa, Gridania, Ul'dah, Ishgard, Idyllshire,
  Rhalgr's Reach, Kugane, Doman Enclave, The Crystarium, Eulmore, Old Sharlayan, Radz-at-Han,
  Tuliyollal and Solution Nine. A city with no lines of its own plays the plain City line. Moving
  around inside the same city (Limsa's Upper to Lower Decks) is silent.
- **Home** is your own house, FC house, private chambers or apartment (found automatically), or any
  place you register as home under **Settings > Homes**. Anyone else's house plays House or
  Apartment ("Pardon the intrusion"). With no Home lines, your House/Apartment lines play.
- **Friends** play their arrival lines when they arrive where you are and you can see them. If you
  arrive together, one of you speaks. A registered home with **Friends live here** ticked plays a
  friend's Home line instead of House.
- The first zone after logging in doesn't count.

**Homes** (Settings): go inside a house or apartment and click **Register this place**. Rename it,
tick **Home** (your Home line plays there) and **Friends live here** (friends' Home lines too).
Registered places can also be picked in a custom event's **Place** condition.

**State** (general events)

You (or a friend near you) start Swimming, Diving, Mounted, Flying, Crafting, Gathering, Fishing,
Performing, or draw your weapon outside combat. Nothing plays in duties or combat, or for something
already going on after a loading screen. Friends' Diving, Flying and Fishing can't be seen from your
game, so those only play for you.

**Time of day** (general events)

Eorzea time moves into a new part of the day: Dawn (5:00), Morning (7:00), Day (12:00), Dusk (17:00),
Evening (19:00), Night (22:00). One voice in the party comments, not in duties or combat. A new period
comes every 9-15 real minutes, so each has a 30-minute cooldown.

**Weather** (general events)

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

**Idle** (general events)

Random chatter while you're standing around. It only starts after you've stood still for a while:
moving, fighting, a duty, crafting or gathering starts the wait over (turning on the spot and emotes
don't). Under Settings > Chatter, choose how often (Off, Rare, Normal, Frequent) and how long you must
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

Job abilities live on each job's page, not on Every job. The Lines page only shows the events that apply to the job you're editing; hover the (i) next to an
event to see exactly which buffs trigger it. For **Kardia** and **Dance partner**, tick a line's
**Initial** box to make it play when you first choose a partner; unticked lines play when you switch. A stance or buff that's
already on when you zone in doesn't trigger a line.

Skill names are looked up from the game in English, so this currently assumes an English client.

## Your own events

Click **+ New** at the bottom of the Lines page to make your own trigger. It opens on the right with
its conditions ready to fill in (**Edit trigger** opens them again later). Your event plays a line the moment
**all** of its conditions are true at the same time. Conditions can be:

- **Casting** an action (anything with a cast bar)
- **Has status**: a game buff or debuff on you. Pick **Player (You)** for a status on you, or **Target** for one you
  put on someone else (a party member or your target), like Haima
- **Doing emote**: e.g. `wave` or `/wave`
- **Has Moodle**: a custom status from the Moodles plugin (needs Moodles; friends' Moodles count
  when they're synced to you)
- **Treasure coffer**: you just opened a coffer (any, regular, or treasure map)
- **Weather**: the current weather is of a kind (Rain, Snow, Fog...)
- **Idle**: you've been standing still (out of combat and duties) for at least N minutes
- **Joining a party**: you just joined someone's party
- **Time of day**: Eorzea time is in any of the periods you tick (Dawn, Morning, Day, Dusk, Evening, Night)
- **Place**: you're in any of the places you tick (City or a specific city, Field, Inn, Residential
  area, Home, House, Apartment, Gold Saucer, Island Sanctuary, Duty, or a registered place)
- **State**: you're doing any of the things you tick (Swimming, Diving, Mounted, Flying, Crafting,
  Gathering, Fishing, Performing, Weapon drawn, In combat). For friends' packs, Diving, Flying and
  Fishing can't be seen.
- **Built-in event**: makes it a *variant* (below)

### Variants

Open any built-in event and click **Make a variant**. You get your own event that starts from it
(say, *Wipe*) where you add extra conditions (a Moodle, a place, the time of day...). When the
built-in event fires and the extra conditions are true, the variant's lines play **instead of** the
built-in ones. Who speaks, cooldowns and everything else work like the built-in event. The new
variant opens straight away; it's listed under **Your events: Variants**, links back to the
original ("Variant of: Wipe"), and the original lists its variants.

When a custom event and a built-in event fire for the same moment (a custom "Panhaimatinon" event
and Sage's Big heal line, say), the custom line wins.

A custom event built on **Treasure coffer** or **Joining a party** plays *instead of* the built-in
coffer or party line, so e.g. "Treasure coffer + Has Moodle *Greedy*" can replace the usual line when
that Moodle is on. Weather and idle conditions fire when they become true (not when you zone into
weather that's already there).

Lines for your events are written right under them (lines under Every job work on any job). Your
events are saved in your voice pack, so friends who import it hear them too. Give them a
**Category** (Emotes, RP, Moodles...) to group them, and switch them **On/Off** without deleting
them.

## Staying organized

The Lines page lists the events you've written lines for (and your own events) on the left, under
headings (Combat & duties, Places, Weather, Job abilities, Your events...). The number is how many
lines it has; a filled dot means they're all recorded, a hollow one means some still need audio.
Click an event to edit it on the right.

**+ Add event** is a searchable list of every event for the job, by category, with what triggers
each one; picking one opens it with a first line ready. Tick **Show all events** to list everything.

## Chatter

**Settings > Chatter** sets how talkative each kind of event is, for every voice you hear (yours and
friends'): Combat & duties, Social, Treasure, Places, State, Time of day, Weather, Job abilities and
Your events can each be **Off**, **Less** (three times as long between lines), **Normal** or **More**
(half as long). Idle has its own setting there: how often (Off, Rare, Normal, Frequent) and how long
you must stand still first. Variants follow the event they're based on.

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
Turn it off on the Settings page if you prefer.

## Volume

Voices play through Windows directly rather than through the game's sound channels. By default
they follow the game's **Master** and **Voice** volume (and go silent when either is muted); turn
that off on the Settings page if you'd rather control them separately. The plugin's own Volume
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

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

### No audio chaos

- **One voice at a time.** All voices share one audio channel and one gap between lines. Only a
  more urgent line can cut in, and it replaces what's playing instead of overlapping.
- **Personal events belong to their owner.** A raise, death, Kardia swap or big heal is only
  voiced by the character who did it.
- **Shared events get one speaker.** Duty start, wipes, clears, tank deaths, clutch saves and big
  pulls are voiced by one party member, picked the same way on everyone's machine (with the same
  line), so everyone hears the same thing.

## Events

| Event | When it fires |
|---|---|
| Duty start | A duty commences |
| Kardia swap (SGE) | Your Kardion moves to another party member |
| Big heal | SGE: Pneuma, Kerachole, Holos, Panhaima. WHM, SCH, AST: starter triggers |
| Clutch save | Someone drops under 15% HP and is back above 50% within 3 seconds |
| Tank death | A tank in the party dies |
| Raise | Someone gets a Raise from you |
| You die | You die |
| Wipe | The party wipes |
| Big pull | 5+ enemies in combat within 30 yalms |
| Duty complete / Clean clear | Duty cleared (clean = no deaths) |

Skill names are looked up from the game in English, so this currently assumes an English client.

## Commands

`/ov` is short for `/outervoice`. (The game already uses `/voice` to mute voices.)

- `/ov`: open the window
- `/ov on` / `/ov off`
- `/ov test <event> [job]`, e.g. `/ov test raise SGE`
- `/ov reload`: re-read your pack after editing files by hand

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

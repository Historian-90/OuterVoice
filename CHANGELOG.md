# Changelog

## 0.9.20
- New **Replies** page (two speech bubbles): browse a friend's lines and add your own replies. Your
  reply plays right after their line finishes, said by your character, whenever you're nearby. Several
  replies per line are picked at random, and a Chance slider sets how often one follows.
- The Lines page now uses a single speech bubble icon.
- Friends need 0.9.20 to hear your replies.

## 0.9.19
- Custom events: the Who box (react to your target, the party or a specific player) is now on
  **Has status**, **Has Moodle**, **Treasure coffer** (regular coffers), **Dies**, **Gets raised** and
  **HP below**. Starts casting, Uses an action and Doing emote are about your own character again.
- **In combat** and **Weapon drawn** are gone from the list; the **State** condition already covers
  them. Events that already use them keep working.
- The condition dropdown is grouped: Actions, Status & health, Moments, Surroundings, Variants.
- Example names in the plugin and README now use Thancred.

## 0.9.18
- Custom events: new **Who** box on many conditions (You, Your target, Anyone in party, or a specific
  player), so your character can react to others, e.g. "When Thancred dies".
- New conditions: **Dies**, **Gets raised**, **HP below**, **In combat** and **Weapon drawn**.
- **+ Or when...** adds another set of conditions; the event plays when any one set is true.
- Friends need 0.9.18 to understand events that use these.

## 0.9.17
- Fixed: custom events cut off a line that had just started (for example Triplecast over your Burst
  line) even with Urgent unticked. Now only Urgent custom events can interrupt.
  If you relied on that (a custom event meant to replace a built-in line), tick Urgent, or make it a
  variant of the built-in event instead.
- Lines no longer repeat back to back: the line picked last time for an event is skipped for the
  next few minutes, and friends still hear the same line as you.
- The random picks are properly shuffled now; before, they could fall into patterns.

## 0.9.16
- New custom event condition **Uses an action**: plays the moment an action goes off, so instant
  skills, oGCD abilities and finished casts can have lines (for you and for friends).
- New custom event condition **Player joins the party**: plays when a specific player joins the
  party you're in. Pick them from your friends or party, or type their name.
- "Casting" is now called **Starts casting**, to tell it apart from Uses an action.

## 0.9.15
- You and your friends now always hear the same line, not just the same voice. Lines can now come
  up twice in a row, so give events that happen often (bursts, idle, weather, duty start) 4 or more
  lines. Events with only a few lines show a reminder on the Lines page.

## 0.9.14
- Fixed: duty start lines were often skipped, because the duty begins right as the loading screen
  ends, while Outer Voice is still keeping quiet. They now play a couple of seconds later.
- Wipe and duty complete lines wait out a cutscene or another line instead of being dropped.

## 0.9.13
- Fixed: lying down on a couch or bed in some poses wasn't recognised, so idle lines could still play.
  Sitting and swimming are now read the same reliable way.
- Settings > Testing shows the pose the plugin sees, to help spot anything it gets wrong.

## 0.9.12
- Characters lying down (in bed, /doze) don't say anything at all, so no more idle chatter while
  you're "asleep" and away from the keyboard.
- Idle lines now have Standing / Sitting / Swimming tick boxes, so you can write lines for idling in
  a chair or floating in water. Existing idle lines count as Standing: tick Sitting or Swimming on
  any that fit those too.

## 0.9.11
- New: while a voice line plays, the game's music, sound effects, ambience, system and performance
  sounds fade down smoothly, then fade back up when the line ends, so whispers and quiet lines are
  easier to hear. On by default; turn it off or set how quiet the rest of the game gets under
  Settings > Playback.

## 0.9.10
- Fixed: the Raise line could play when someone joined the party mid-duty, or when a party member
  came back into range (for example, rejoining you after leaving an instance later). It now only
  plays for someone you actually saw fall.
- Party members who are away no longer count for deaths or HP changes until they're back near you.

## 0.9.9
- Fixed: a party member in another zone could be picked to comment on your weather, time of day
  and other shared moments. Only friends who are actually around you speak now.

## 0.9.8
- Idle lines are now personal: each character speaks when *they* have been standing still, and
  friends hear your idle lines at the same moment you do.
- Friends standing near you now count even when you're not in a party with them.
- Your idle settings travel with your shared pack. After updating, press Publish now once so
  friends get them.

## 0.9.7
- Fixed: arrival lines (Island Sanctuary, cities, homes...) could be lost right after a loading
  screen, and the plugin then treated later arrivals there as "same place" and stayed silent.
- If another line is playing when you arrive somewhere, the arrival line now waits and plays
  shortly after instead of being dropped.
- Talking to an NPC no longer counts as arriving in the zone again.
- Arrival notes now show in /xllog, including why a line didn't play.

## 0.9.6
- A "What's new" window now opens after an update, listing what changed. Open it again any time
  from Settings > About.
- Clearer sharing instructions: Dropbox and OneDrive sharing uses the folder their desktop app syncs
  on your PC, not the website.

## 0.9.5
- New: Voice Pack > Clean up finds recordings that no line uses and moves them to the Recycle Bin.
- The Jobs table on the Voice Pack page no longer lists jobs that have no lines.

## 0.9.4
- Unsaved changes are no longer lost when the plugin updates or closes: they're kept, and the window
  offers to restore them next time.
- Searching the Lines page only lists events that actually match.

## 0.9.3
- Older lines get ids that match their event (start_01 becomes duty_start_01), with recordings
  renamed to match. The previous pack file is kept as voicepack.json.before-id-tidy.

## 0.9.2
- Each line's job box is now tick boxes: Every job, a role (Tanks, Healers, Melee, Ranged, Casters)
  or any mix of jobs.

## 0.9.1
- Every job lines play on every job, mixed with a job's own lines.

## 0.9.0
- Each line picks the job it plays on (this replaces the mood box).
- The job dropdown on the Lines page is now a Show filter: All lines, Every job, or one job.

## 0.8.5
- Island Sanctuary lines play again (it was mistaken for a duty).
- Settings > Testing shows where the plugin thinks you are and what the last arrival check did.

## 0.8.0 - 0.8.4
- Portraits: your character's picture in the game's battle-talk pop-up (needs Penumbra), with size
  and position sliders. It travels with your pack.
- Box style: Parchment, Frosted, Shadow or Classic blue.

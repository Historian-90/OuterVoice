# Changelog

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

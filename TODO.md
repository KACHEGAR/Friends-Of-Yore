# Open issues

## Grabbing a friend's hand broke in 0.29.1 (reported 2026-10-29) - 0.29.2 drops the 0.29.1 change
- 0.29.2 is built on 0.29.0: the player on a stone pins the friend's hand where it was caught (relative to
  their own hand on the hold), which breaks the echo loop that made their hand creep toward their face.
- The owner reports a problem with grabbing after 0.29.1; details not yet given.
- 0.29.1 changed only `RemotePlayer.GripPoint` (FixedUpdate): while we hold a friend's hand AND their flag says
  that hand holds us, the grip point is frozen at `friend body + offset` taken at the catch, until we let go.
  Suspects: the offset is world-space (no rotation with the friend's yaw); the lock also moves the catch zone
  collider, so `Guard`/`HandWithdrawn`/catch-gap checks may now disagree with where the hand is drawn;
  the lock is sticky until release, even if the friend lets go and moves the hand away.
- Fallback: revert cc9cb2e (back to 0.29.0 behaviour, the jerks come back).
- Needed from the test: what exactly happens (can't grab? drops? flies off?), who was on the stone,
  LogOutput.log + friendsofyore-trace.csv from both PCs (look for "grip point fixed to their body").

## Ideas
- Grip point on a moving hold (skeleton): rotate the offset with the friend's body yaw.

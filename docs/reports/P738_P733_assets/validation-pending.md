The once-per-assembly Core → ClientCore → MonoGame sweep is in progress.
The two Core repository guards that launch git (`MeshyGuardTests` and
`PrivatePathGuardTests`) are excluded to honor the user's explicit prohibition.
Ordinary local/loopback and LiveTiming tests run. The local P542, P660, P496,
P539 baseline and P539c opt-ins are enabled; historical 221/200v200 opt-in
proof drivers remain off as directed. No assertion, timeout or quarantine
trait was changed.

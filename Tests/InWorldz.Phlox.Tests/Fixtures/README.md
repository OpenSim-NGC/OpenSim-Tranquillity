# Fixtures

Scripts committed here are **small, in-world scripts**, kept because a compiler defect
was found in them and a test has to keep compiling the exact text that failed. They are stored **verbatim**
— not tidied, not reformatted — because the whitespace and the ordering are part of what is being tested.

Residents are not named. Where a script came from a resident's prim it is credited only as *a
resident script*.

Residents' content in bulk (a survey of every script running on a grid) is never committed. A fixture here is a single script that pins a specific defect, and the test is
worthless without the body, so the body is committed.

| file | asset | why it is here |
|---|---|---|
| `lmap4.lsl` | - | A four-state lamp written for these tests (`lmap4`): touch moves through three brightness states and back to default. The compiler once crashed with a `NullReferenceException` on a script of this shape. sha256 `ef100184bd43ce2a3255cc697f57547c39343c9460295849594fc3d7f98c4fa9`. |

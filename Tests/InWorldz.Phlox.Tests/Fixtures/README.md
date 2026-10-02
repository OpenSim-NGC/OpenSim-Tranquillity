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
| `lmap4.lsl` | `1ee3b9b1-39c7-4f20-a5f3-6dbd79046218` | A four-state lamp from a resident's prim (`lmap4`). Crashed the Phlox compiler with a `NullReferenceException` on every region start. sha256 `d92967d068e5f693ccf5de669659cd3522f672290f54e7005ff4d7c27699f352`. |

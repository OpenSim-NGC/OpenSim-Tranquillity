// phlox22-syntaxerror.lsl - PHLOX-22 C: the ';' after llOwnerSay(...) is missing. Saving it
// must show the compile error in the editor's error pane, not "compiled".
default
{
    state_entry()
    {
        llOwnerSay("this line has no semicolon")
    }
}

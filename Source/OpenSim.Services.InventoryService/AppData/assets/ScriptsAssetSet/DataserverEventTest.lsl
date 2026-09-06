string NOTECARD_NAME = "DataserverTestNotecard";

key gAgentDataQuery;
key gNotecardLineQuery;
integer gSawAgentData;
integer gSawNotecardLine;

start_test()
{
    gSawAgentData = FALSE;
    gSawNotecardLine = FALSE;

    llOwnerSay("Starting dataserver event test.");
    llOwnerSay("Put a notecard named '" + NOTECARD_NAME + "' in this object with at least one line.");

    gAgentDataQuery = llRequestAgentData(llGetOwner(), DATA_NAME);
    llOwnerSay("llRequestAgentData query id: " + (string)gAgentDataQuery);

    gNotecardLineQuery = llGetNotecardLine(NOTECARD_NAME, 0);
    llOwnerSay("llGetNotecardLine query id: " + (string)gNotecardLineQuery);
}

report_if_done()
{
    if (gSawAgentData && gSawNotecardLine)
    {
        llOwnerSay("PASS: both llRequestAgentData and llGetNotecardLine triggered dataserver events.");
    }
}

default
{
    state_entry()
    {
        start_test();
    }

    touch_start(integer count)
    {
        start_test();
    }

    dataserver(key query_id, string data)
    {
        if (query_id == gAgentDataQuery)
        {
            gSawAgentData = TRUE;
            llOwnerSay("dataserver event for llRequestAgentData: " + data);
            report_if_done();
            return;
        }

        if (query_id == gNotecardLineQuery)
        {
            gSawNotecardLine = TRUE;
            llOwnerSay("dataserver event for llGetNotecardLine: " + data);
            report_if_done();
            return;
        }

        llOwnerSay("Unexpected dataserver event. query id: " + (string)query_id + ", data: " + data);
    }
}
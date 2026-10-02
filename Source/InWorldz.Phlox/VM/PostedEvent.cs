using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.VM
{
    public class PostedEvent
    {
        public const int NO_TRANSITION = -1;

        public SupportedEventList.Events EventType;
        public object[] Args;
        public DetectVariables[] DetectVars;
        public int TransitionToState = NO_TRANSITION;

        /// <summary>
        /// Invoked exactly once by the scheduler when this event is DONE with - its handler
        /// finished, or it was dropped (no handler, script disabled, queue full, script not loaded,
        /// terminated). Not serialized. on_damage is the one SL event the region waits on.
        /// </summary>
        public Action Completed;

        public void SignalCompleted()
        {
            Action c = Completed;
            Completed = null;
            c?.Invoke();
        }

        public void Normalize()
        {
            if (Args != null)
            {
                for (int i = 0; i < Args.Length; i++)
                {
                    object obj = Args[i];

                    object[] objarr = obj as object[];
                    if (objarr != null)
                    {
                        Args[i] = new LSLList(objarr);
                    }
                }
            }
        }
    }
}

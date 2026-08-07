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

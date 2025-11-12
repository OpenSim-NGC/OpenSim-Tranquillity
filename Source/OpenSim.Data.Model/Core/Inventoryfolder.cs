using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class Inventoryfolder
{
    public string FolderName { get; set; }
    public short Type { get; set; }
    public int Version { get; set; }
    public string FolderId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string AgentId { get; set; }
    public string ParentFolderId { get; set; }
}

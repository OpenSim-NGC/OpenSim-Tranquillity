namespace OpenSim.Data.Model.Core;

public partial class Experience
{
    public string public_id { get; set; }
    public string owner_id { get; set; }
    public string name { get; set; }
    public string description { get; set; }
    public string group_id { get; set; }
    public string logo { get; set; }
    public string marketplace { get; set; }
    public string slurl { get; set; }
    public int maturity { get; set; }
    public int properties { get; set; }    
}

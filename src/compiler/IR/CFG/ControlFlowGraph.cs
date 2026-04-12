namespace PyMCU.IR.CFG;

public class ControlFlowGraph
{
    public BasicBlock Entry { get; set; } = null!;
    public List<BasicBlock> Blocks { get; set; } = [];
}
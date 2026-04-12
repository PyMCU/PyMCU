namespace PyMCU.IR.CFG;

public class BasicBlock(string name)
{
    public string Name { get; set; } = name;
    public List<Instruction> Instructions { get; set; } = [];
    public List<BasicBlock> Predecessors { get; set; } = [];
    public List<BasicBlock> Successors { get; set; } = [];
}
#ifndef PIC18PEEPHOLE_H
#define PIC18PEEPHOLE_H

#include <string>
#include <vector>

struct PIC18AsmLine {
    enum Type { INSTRUCTION, LABEL, COMMENT, RAW, EMPTY };

    Type type;
    std::string label;
    std::string mnemonic;
    std::string op1;
    std::string op2;
    std::string op3; // PIC18 sometimes has 3 operands (e.g. MOVFF)
    std::string content;

    static PIC18AsmLine Instruction(std::string m, std::string o1 = "", std::string o2 = "", std::string o3 = "") {
        return {INSTRUCTION, "", m, o1, o2, o3, ""};
    }

    static PIC18AsmLine Label(std::string l) {
        return {LABEL, l, "", "", "", "", ""};
    }

    static PIC18AsmLine Comment(std::string c) {
        return {COMMENT, "", "", "", "", "", c};
    }

    static PIC18AsmLine Raw(std::string r) {
        return {RAW, "", "", "", "", "", r};
    }

    static PIC18AsmLine Empty() {
        return {EMPTY, "", "", "", "", "", ""};
    }

    std::string to_string() const;
};

class PIC18Peephole {
public:
    static std::vector<PIC18AsmLine> optimize(const std::vector<PIC18AsmLine> &lines);
};

#endif // PIC18PEEPHOLE_H
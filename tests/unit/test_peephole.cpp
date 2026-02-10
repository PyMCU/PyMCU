#include <gtest/gtest.h>
#include "backend/targets/pic14/PIC14Peephole.h"

TEST(PIC14PeepholeTest, RedundantMovf) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("MOVWF", "0x20"),
        PIC14AsmLine::Instruction("MOVF", "0x20", "W")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 1);
    EXPECT_EQ(optimized[0].mnemonic, "MOVWF");
}

TEST(PIC14PeepholeTest, RedundantMovlw) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("MOVLW", "0x00"),
        PIC14AsmLine::Instruction("MOVWF", "0x20"),
        PIC14AsmLine::Instruction("MOVLW", "0x00"),
        PIC14AsmLine::Instruction("MOVWF", "0x21")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 3);
    EXPECT_EQ(optimized[0].mnemonic, "MOVLW");
    EXPECT_EQ(optimized[1].mnemonic, "MOVWF");
    EXPECT_EQ(optimized[2].mnemonic, "MOVWF");
}

TEST(PIC14PeepholeTest, RedundantBankSwitch) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("BSF", "STATUS", "5"),
        PIC14AsmLine::Instruction("MOVLW", "0xFF"),
        PIC14AsmLine::Instruction("BSF", "STATUS", "5")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 2);
    EXPECT_EQ(optimized[0].mnemonic, "BSF");
    EXPECT_EQ(optimized[1].mnemonic, "MOVLW");
}

TEST(PIC14PeepholeTest, RedundantIorlw) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("MOVF", "0x20", "W"),
        PIC14AsmLine::Instruction("IORLW", "0")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 1);
    EXPECT_EQ(optimized[0].mnemonic, "MOVF");
}

TEST(PIC14PeepholeTest, GotoNextLabel) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("GOTO", "L1"),
        PIC14AsmLine::Comment("Some comment"),
        PIC14AsmLine::Label("L1")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 2);
    EXPECT_EQ(optimized[0].type, PIC14AsmLine::COMMENT);
    EXPECT_EQ(optimized[1].type, PIC14AsmLine::LABEL);
}

TEST(PIC14PeepholeTest, LabelResetsState) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("MOVLW", "0x00"),
        PIC14AsmLine::Label("L1"),
        PIC14AsmLine::Instruction("MOVLW", "0x00")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 3); // Second MOVLW should NOT be removed
}

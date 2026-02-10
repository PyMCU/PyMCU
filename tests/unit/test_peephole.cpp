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

TEST(PIC14PeepholeTest, RedundantStore) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("MOVF", "0x20", "W"),
        PIC14AsmLine::Instruction("MOVWF", "0x20")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 1);
    EXPECT_EQ(optimized[0].mnemonic, "MOVF");
}

TEST(PIC14PeepholeTest, DeadCodeAfterReturn) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("RETURN"),
        PIC14AsmLine::Instruction("MOVLW", "0x00"),
        PIC14AsmLine::Label("L1")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 2);
    EXPECT_EQ(optimized[0].mnemonic, "RETURN");
    EXPECT_EQ(optimized[1].type, PIC14AsmLine::LABEL);
}

TEST(PIC14PeepholeTest, MathIdentities) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("MOVF", "0x20", "W"),
        PIC14AsmLine::Instruction("ADDLW", "0"),
        PIC14AsmLine::Instruction("IORLW", "0"),
        PIC14AsmLine::Instruction("XORLW", "0"),
        PIC14AsmLine::Instruction("ANDLW", "0xFF"),
        PIC14AsmLine::Instruction("MOVLW", "0x01")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 2);
    EXPECT_EQ(optimized[0].mnemonic, "MOVF");
    EXPECT_EQ(optimized[1].mnemonic, "MOVLW");
}

TEST(PIC14PeepholeTest, BitCoalescingRedundant) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("BCF", "0x20", "1"),
        PIC14AsmLine::Instruction("BSF", "0x20", "1")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 1);
    EXPECT_EQ(optimized[0].mnemonic, "BSF");
}

TEST(PIC14PeepholeTest, JumpToNextWithSkip) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("BTFSC", "STATUS", "2"),
        PIC14AsmLine::Instruction("GOTO", "L1"),
        PIC14AsmLine::Label("L1")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    // Ideally it should be empty or just the label.
    // If GOTO L1 is removed, BTFSC must also be removed.
    ASSERT_EQ(optimized.size(), 1);
    EXPECT_EQ(optimized[0].type, PIC14AsmLine::LABEL);
}

TEST(PIC14PeepholeTest, GotoNextLabelMultiple) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("GOTO", "L11"),
        PIC14AsmLine::Label("L10"),
        PIC14AsmLine::Label("L11")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    // GOTO L11 should be removed because L11 is effectively the next instruction
    ASSERT_EQ(optimized.size(), 2);
    EXPECT_EQ(optimized[0].label, "L10");
    EXPECT_EQ(optimized[1].label, "L11");
}

TEST(PIC14PeepholeTest, DeadLabelElimination) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("GOTO", "L.1"),
        PIC14AsmLine::Label("L.2"), // Dead label
        PIC14AsmLine::Label("L.1"),
        PIC14AsmLine::Instruction("RETURN"),
        PIC14AsmLine::Label("ExternalEntryPoint"), // Not internal
        PIC14AsmLine::Instruction("GOTO", "L.1") // Keep L.1 alive
    };
    auto optimized = PIC14Peephole::optimize(lines);
    // L.2 should be removed
    // First GOTO L.1 should also be removed.
    // So we should have: L.1, RETURN, ExternalEntryPoint, GOTO L.1
    ASSERT_EQ(optimized.size(), 4);
    EXPECT_EQ(optimized[0].label, "L.1");
    EXPECT_EQ(optimized[1].mnemonic, "RETURN");
    EXPECT_EQ(optimized[2].label, "ExternalEntryPoint");
    EXPECT_EQ(optimized[3].mnemonic, "GOTO");
}

TEST(PIC14PeepholeTest, SkipDoesNotAllowDeadCodeRemoval) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("BTFSC", "STATUS", "2"),
        PIC14AsmLine::Instruction("RETURN"),
        PIC14AsmLine::Instruction("MOVLW", "0x01"), // This should NOT be removed
        PIC14AsmLine::Label("L1")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    ASSERT_EQ(optimized.size(), 4);
    EXPECT_EQ(optimized[2].mnemonic, "MOVLW");
}

TEST(PIC14PeepholeTest, BitCoalescingMultipleBits) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("BSF", "0x85", "0"),
        PIC14AsmLine::Instruction("BSF", "0x85", "1"),
        PIC14AsmLine::Instruction("BSF", "0x85", "2"),
        PIC14AsmLine::Instruction("BSF", "0x85", "3")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    // Should be:
    // MOVLW 0x0F
    // IORWF 0x85, F
    ASSERT_EQ(optimized.size(), 2);
    EXPECT_EQ(optimized[0].mnemonic, "MOVLW");
    EXPECT_EQ(optimized[0].op1, "0x0F");
    EXPECT_EQ(optimized[1].mnemonic, "IORWF");
    EXPECT_EQ(optimized[1].op1, "0x85");
    EXPECT_EQ(optimized[1].op2, "F");
}

TEST(PIC14PeepholeTest, BCFCoalescingMultipleBits) {
    std::vector<PIC14AsmLine> lines = {
        PIC14AsmLine::Instruction("BCF", "0x85", "0"),
        PIC14AsmLine::Instruction("BCF", "0x85", "1"),
        PIC14AsmLine::Instruction("BCF", "0x85", "2")
    };
    auto optimized = PIC14Peephole::optimize(lines);
    // Should be:
    // MOVLW 0xF8
    // ANDWF 0x85, F
    ASSERT_EQ(optimized.size(), 2);
    EXPECT_EQ(optimized[0].mnemonic, "MOVLW");
    EXPECT_EQ(optimized[0].op1, "0xF8");
    EXPECT_EQ(optimized[1].mnemonic, "ANDWF");
    EXPECT_EQ(optimized[1].op1, "0x85");
    EXPECT_EQ(optimized[1].op2, "F");
}

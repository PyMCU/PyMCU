#include <gtest/gtest.h>
#include "Diagnostic.h"

// Since get_line is private, we can either make it public for testing,
// or test it via a public method if possible. 
// However, Diagnostic::report prints to std::cerr which is hard to capture without redirections.
// Let's make a testable version or use a friend class.
// For simplicity in this task, I'll assume I can modify Diagnostic.h or just test what's public.
// Wait, I can't easily test report() output without capturing stderr.
// Let's refactor Diagnostic.h to make get_line public or at least more testable.

class DiagnosticTest : public ::testing::Test {
protected:
    // Helper to access private member if needed, but I'll prefer making it public or testing report.
};

// I will temporarily make get_line public in Diagnostic.h to test it properly, 
// as it contains important logic for error reporting.

#define private public
#include "Diagnostic.h"
#undef private

TEST(DiagnosticTest, GetLine) {
    std::string_view source = "line 1\nline 2\nline 3";
    
    EXPECT_EQ(Diagnostic::get_line(source, 1), "line 1");
    EXPECT_EQ(Diagnostic::get_line(source, 2), "line 2");
    EXPECT_EQ(Diagnostic::get_line(source, 3), "line 3");
    EXPECT_EQ(Diagnostic::get_line(source, 4), "");
}

TEST(DiagnosticTest, GetLineEmpty) {
    std::string_view source = "";
    EXPECT_EQ(Diagnostic::get_line(source, 1), "");
}

TEST(DiagnosticTest, GetLineTrailingNewline) {
    std::string_view source = "line 1\n";
    EXPECT_EQ(Diagnostic::get_line(source, 1), "line 1");
    EXPECT_EQ(Diagnostic::get_line(source, 2), "");
}

#include <gtest/gtest.h>
#include "Utils.h"
#include <fstream>
#include <filesystem>

TEST(UtilsTest, ReadSourceValidFile) {
    std::string filename = "test_source.py";
    std::string content = "def main():\n    return 0";

    std::ofstream out(filename);
    out << content;
    out.close();

    EXPECT_EQ(read_source(filename), content);

    std::filesystem::remove(filename);
}

TEST(UtilsTest, ReadSourceInvalidFile) {
    EXPECT_THROW(read_source("non_existent_file.py"), std::runtime_error);
}
package dev.begeistert.pymcu

import dev.begeistert.pymcu.config.PyMcuConfigReader
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class PyMcuConfigReaderTest {

    private val sampleToml = """
        [build-system]
        requires = ["setuptools"]

        [project]
        name = "my-mcu-project"
        version = "0.1.0"

        [tool.pymcu]
        chip = "atmega328p"
        frequency = "16000000"
        sources = "src"
        entry = "main.py"

        [tool.other]
        something = "irrelevant"
    """.trimIndent()

    @Test
    fun `parses chip and frequency from tool_pymcu section`() {
        val config = PyMcuConfigReader.parseContent(sampleToml)
        assertNotNull(config)
        assertEquals("atmega328p", config!!.chip)
        assertEquals("16000000", config.frequency)
    }

    @Test
    fun `parses sources and entry`() {
        val config = PyMcuConfigReader.parseContent(sampleToml)
        assertEquals("src", config!!.sources)
        assertEquals("main.py", config.entry)
    }

    @Test
    fun `returns null when section is absent`() {
        val toml = """
            [project]
            name = "other"
        """.trimIndent()
        assertNull(PyMcuConfigReader.parseContent(toml))
    }

    @Test
    fun `returns null for empty string`() {
        assertNull(PyMcuConfigReader.parseContent(""))
    }

    @Test
    fun `does not bleed into next section`() {
        val toml = """
            [tool.pymcu]
            chip = "attiny85"

            [tool.other]
            chip = "should_not_appear"
        """.trimIndent()
        val config = PyMcuConfigReader.parseContent(toml)
        assertEquals("attiny85", config!!.chip)
    }

    @Test
    fun `handles missing optional fields`() {
        val toml = """
            [tool.pymcu]
            chip = "attiny85"
        """.trimIndent()
        val config = PyMcuConfigReader.parseContent(toml)
        assertNotNull(config)
        assertEquals("attiny85", config!!.chip)
        assertNull(config.frequency)
        assertNull(config.sources)
        assertNull(config.entry)
    }
}

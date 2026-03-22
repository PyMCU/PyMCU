package dev.begeistert.whipsnake

import dev.begeistert.whipsnake.config.WhipsnakeConfigReader
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class WhipsnakeConfigReaderTest {

    private val sampleToml = """
        [build-system]
        requires = ["setuptools"]

        [project]
        name = "my-mcu-project"
        version = "0.1.0"

        [tool.whip]
        chip = "atmega328p"
        frequency = "16000000"
        sources = "src"
        entry = "main.py"

        [tool.other]
        something = "irrelevant"
    """.trimIndent()

    @Test
    fun `parses chip and frequency from tool_pymcu section`() {
        val config = WhipsnakeConfigReader.parseContent(sampleToml)
        assertNotNull(config)
        assertEquals("atmega328p", config!!.chip)
        assertEquals("16000000", config.frequency)
    }

    @Test
    fun `parses sources and entry`() {
        val config = WhipsnakeConfigReader.parseContent(sampleToml)
        assertEquals("src", config!!.sources)
        assertEquals("main.py", config.entry)
    }

    @Test
    fun `returns null when section is absent`() {
        val toml = """
            [project]
            name = "other"
        """.trimIndent()
        assertNull(WhipsnakeConfigReader.parseContent(toml))
    }

    @Test
    fun `returns null for empty string`() {
        assertNull(WhipsnakeConfigReader.parseContent(""))
    }

    @Test
    fun `does not bleed into next section`() {
        val toml = """
            [tool.whip]
            chip = "attiny85"

            [tool.other]
            chip = "should_not_appear"
        """.trimIndent()
        val config = WhipsnakeConfigReader.parseContent(toml)
        assertEquals("attiny85", config!!.chip)
    }

    @Test
    fun `handles missing optional fields`() {
        val toml = """
            [tool.whip]
            chip = "attiny85"
        """.trimIndent()
        val config = WhipsnakeConfigReader.parseContent(toml)
        assertNotNull(config)
        assertEquals("attiny85", config!!.chip)
        assertNull(config.frequency)
        assertNull(config.sources)
        assertNull(config.entry)
    }

    @Test
    fun `parses board field`() {
        val toml = """
            [tool.whip]
            board = "arduino_uno"
            frequency = "16000000"
            sources = "src"
            entry = "main.py"
        """.trimIndent()
        val config = WhipsnakeConfigReader.parseContent(toml)
        assertNotNull(config)
        assertEquals("arduino_uno", config!!.board)
        assertNull(config.chip)
    }

    @Test
    fun `displayName resolves board alias`() {
        val toml = """
            [tool.whip]
            board = "arduino_uno"
        """.trimIndent()
        val config = WhipsnakeConfigReader.parseContent(toml)!!
        assertEquals("Arduino Uno (atmega328p)", config.displayName)
    }

    @Test
    fun `displayName falls back to chip name`() {
        val toml = """
            [tool.whip]
            chip = "attiny85"
        """.trimIndent()
        val config = WhipsnakeConfigReader.parseContent(toml)!!
        assertEquals("attiny85", config.displayName)
    }

    @Test
    fun `parses stdlib array single value`() {
        val toml = """
            [tool.whip]
            chip = "atmega328p"
            stdlib = ["micropython"]
        """.trimIndent()
        val config = WhipsnakeConfigReader.parseContent(toml)!!
        assertEquals(listOf("micropython"), config.stdlib)
    }

    @Test
    fun `parses stdlib array multiple values`() {
        val toml = """
            [tool.whip]
            chip = "atmega328p"
            stdlib = ["circuitpython", "extra"]
        """.trimIndent()
        val config = WhipsnakeConfigReader.parseContent(toml)!!
        assertEquals(listOf("circuitpython", "extra"), config.stdlib)
    }

    @Test
    fun `stdlib defaults to empty when absent`() {
        val config = WhipsnakeConfigReader.parseContent(sampleToml)!!
        assertEquals(emptyList<String>(), config.stdlib)
    }

    @Test
    fun `detects ffi section`() {
        val toml = """
            [tool.whip]
            chip = "atmega328p"

            [tool.pymcu.ffi]
            sources = ["lib.c"]
        """.trimIndent()
        val config = WhipsnakeConfigReader.parseContent(toml)!!
        assertEquals(true, config.hasFfi)
    }

    @Test
    fun `hasFfi is false when ffi section absent`() {
        val config = WhipsnakeConfigReader.parseContent(sampleToml)!!
        assertEquals(false, config.hasFfi)
    }
}

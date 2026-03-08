package dev.begeistert.pymcu.settings

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.util.xmlb.XmlSerializerUtil

/**
 * Application-level persistent settings for the PyMCU plugin.
 * Stored in pymcu.xml inside the IDE config directory.
 */
@Service(Service.Level.APP)
@State(
    name = "PyMcuSettings",
    storages = [Storage("pymcu.xml")]
)
class PyMcuSettings : PersistentStateComponent<PyMcuSettings> {

    /** Path to the `pymcu` executable. Defaults to bare name (resolved via PATH). */
    var executablePath: String = "pymcu"

    /**
     * Package manager used for dependency sync.
     * One of: uv, pip, poetry, pipenv.
     */
    var packageManager: String = "uv"

    override fun getState(): PyMcuSettings = this

    override fun loadState(state: PyMcuSettings) {
        XmlSerializerUtil.copyBean(state, this)
    }

    companion object {
        fun getInstance(): PyMcuSettings =
            ApplicationManager.getApplication().getService(PyMcuSettings::class.java)
    }
}

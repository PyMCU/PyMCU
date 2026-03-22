package dev.begeistert.whipsnake.settings

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.util.xmlb.XmlSerializerUtil

/**
 * Application-level persistent settings for the Whipsnake plugin.
 * Stored in pymcu.xml inside the IDE config directory.
 */
@Service(Service.Level.APP)
@State(
    name = "WhipsnakeSettings",
    storages = [Storage("pymcu.xml")]
)
class WhipsnakeSettings : PersistentStateComponent<WhipsnakeSettings> {

    /** Path to the `pymcu` executable. Defaults to bare name (resolved via PATH). */
    var executablePath: String = "whipsnake"

    /**
     * Package manager used for dependency sync.
     * One of: uv, pip, poetry, pipenv.
     */
    var packageManager: String = "uv"

    override fun getState(): WhipsnakeSettings = this

    override fun loadState(state: WhipsnakeSettings) {
        XmlSerializerUtil.copyBean(state, this)
    }

    companion object {
        fun getInstance(): WhipsnakeSettings =
            ApplicationManager.getApplication().getService(WhipsnakeSettings::class.java)
    }
}

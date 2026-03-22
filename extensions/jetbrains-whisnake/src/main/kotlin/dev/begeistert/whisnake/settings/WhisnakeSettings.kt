package dev.begeistert.whisnake.settings

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.util.xmlb.XmlSerializerUtil

/**
 * Application-level persistent settings for the Whisnake plugin.
 * Stored in pymcu.xml inside the IDE config directory.
 */
@Service(Service.Level.APP)
@State(
    name = "WhisnakeSettings",
    storages = [Storage("pymcu.xml")]
)
class WhisnakeSettings : PersistentStateComponent<WhisnakeSettings> {

    /** Path to the `pymcu` executable. Defaults to bare name (resolved via PATH). */
    var executablePath: String = "whisnake"

    /**
     * Package manager used for dependency sync.
     * One of: uv, pip, poetry, pipenv.
     */
    var packageManager: String = "uv"

    override fun getState(): WhisnakeSettings = this

    override fun loadState(state: WhisnakeSettings) {
        XmlSerializerUtil.copyBean(state, this)
    }

    companion object {
        fun getInstance(): WhisnakeSettings =
            ApplicationManager.getApplication().getService(WhisnakeSettings::class.java)
    }
}

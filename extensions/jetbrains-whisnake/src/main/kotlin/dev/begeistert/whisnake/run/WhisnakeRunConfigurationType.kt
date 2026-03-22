package dev.begeistert.whisnake.run

import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.execution.configurations.ConfigurationType
import com.intellij.execution.configurations.RunConfiguration
import com.intellij.icons.AllIcons
import com.intellij.openapi.project.Project
import javax.swing.Icon

/**
 * Registers the "Whisnake" run configuration type in the IDE's run/debug framework.
 */
class WhisnakeRunConfigurationType : ConfigurationType {

    private val factory = object : ConfigurationFactory(this) {
        override fun getId(): String = "WhisnakeConfigurationFactory"

        override fun createTemplateConfiguration(project: Project): RunConfiguration =
            WhisnakeRunConfiguration(project, this, "Whisnake")

        override fun getName(): String = "Whisnake"
    }

    override fun getDisplayName(): String = "Whisnake"

    override fun getConfigurationTypeDescription(): String =
        "Run whip build, flash, or clean commands"

    override fun getIcon(): Icon = AllIcons.RunConfigurations.Application

    override fun getId(): String = "WhisnakeRunConfiguration"

    override fun getConfigurationFactories(): Array<ConfigurationFactory> = arrayOf(factory)

    fun getFactory(): ConfigurationFactory = factory
}

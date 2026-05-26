package dev.begeistert.pymcu.debug

import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.execution.configurations.ConfigurationType
import com.intellij.execution.configurations.RunConfiguration
import com.intellij.icons.AllIcons
import com.intellij.openapi.project.Project
import javax.swing.Icon

class PyMcuDebugConfigurationType : ConfigurationType {

    private val factory = object : ConfigurationFactory(this) {
        override fun getId(): String = "PyMcuDebugConfigurationFactory"

        override fun createTemplateConfiguration(project: Project): RunConfiguration =
            PyMcuDebugConfiguration(project, this, "PyMCU Emulator Debug")

        override fun getName(): String = "PyMCU Emulator Debug"
    }

    override fun getDisplayName(): String = "PyMCU Emulator Debug"

    override fun getConfigurationTypeDescription(): String =
        "Debug PyMCU firmware in the AVR emulator"

    override fun getIcon(): Icon = AllIcons.Actions.StartDebugger

    override fun getId(): String = "PyMcuDebugConfiguration"

    override fun getConfigurationFactories(): Array<ConfigurationFactory> = arrayOf(factory)

    fun getFactory(): ConfigurationFactory = factory
}

import org.jetbrains.intellij.platform.gradle.TestFrameworkType

plugins {
    id("org.jetbrains.intellij.platform") version "2.2.1"
    kotlin("jvm") version "1.9.25"
}

group = "dev.begeistert.pymcu"
version = "0.0.1"

kotlin {
    jvmToolchain(21)
}

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        pycharmCommunity("2024.3")
        bundledPlugin("PythonCore")
        testFramework(TestFrameworkType.Platform)
    }
    testImplementation("junit:junit:4.13.2")
}

intellijPlatform {
    pluginConfiguration {
        id = "dev.begeistert.pymcu"
        name = "PyMCU"
        version = project.version.toString()
    }

    buildSearchableOptions = false
}

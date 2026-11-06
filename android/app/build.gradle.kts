import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

// One version for the Windows app and this APK, from version.txt at the repository root.
// 1.2.3 -> versionCode 10203, so every release installs over the previous one.
val appVersion = rootDir.resolve("../version.txt").readText().trim()
val appVersionCode = appVersion.split('.').map { it.toInt() }.let { (major, minor, patch) -> major * 10000 + minor * 100 + patch }

// Release signing key, never in the repository: the release workflow passes it in ALTROVE_* variables
// (from the GitHub secrets), a local build reads ~/.altrove/signing.properties.
val signing = Properties().apply {
    val file = File(System.getProperty("user.home"), ".altrove/signing.properties")
    if (file.exists()) file.inputStream().use(::load)
    System.getenv("ALTROVE_KEYSTORE")?.let { setProperty("storeFile", it) }
    System.getenv("ALTROVE_KEYSTORE_PASSWORD")?.let { setProperty("storePassword", it) }
    System.getenv("ALTROVE_KEY_ALIAS")?.let { setProperty("keyAlias", it) }
    System.getenv("ALTROVE_KEY_PASSWORD")?.let { setProperty("keyPassword", it) }
}
val hasReleaseKey = signing.getProperty("storeFile")?.let { File(it).exists() } == true

android {
    // Fixed forever since the first release: Android treats another id as another app.
    namespace = "com.altrove.app"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.altrove.app"
        // Android 8: NSD attributes, MediaCodec low-latency keys are ignored where unknown.
        minSdk = 26
        targetSdk = 36
        versionCode = appVersionCode
        versionName = appVersion
        // Where the in-app update looks for the latest release; -PupdateUrl=... points a test build elsewhere.
        val updateUrl = providers.gradleProperty("updateUrl")
            .getOrElse("https://api.github.com/repos/LoLler111/Altrove/releases/latest")
        buildConfigField("String", "LATEST_RELEASE_URL", "\"$updateUrl\"")
    }

    signingConfigs {
        if (hasReleaseKey) create("release") {
            storeFile = File(signing.getProperty("storeFile"))
            storePassword = signing.getProperty("storePassword")
            keyAlias = signing.getProperty("keyAlias")
            keyPassword = signing.getProperty("keyPassword")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            // The in-app update installs a new APK over the old one only when both carry the same
            // signature: releases always use the release key. Without it the APK is signed with the
            // debug key and cannot update a released one.
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2025.08.00")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui-tooling-preview")
    debugImplementation("androidx.compose.ui:ui-tooling")

    implementation("androidx.core:core-ktx:1.16.0")
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.2")

    implementation("org.java-websocket:Java-WebSocket:1.5.7")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.9.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")

    testImplementation("junit:junit:4.13.2")
}

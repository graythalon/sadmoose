plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }

android {
    namespace = "com.graythalon.drift"
    compileSdk = 35
    defaultConfig {
        applicationId = "com.graythalon.drift"
        minSdk = 34
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0"
    }
    kotlinOptions { jvmTarget = "17" }
}

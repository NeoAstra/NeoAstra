# The native library of NeoAstra reaches these members through JNI by name, and registers the native methods by name.
-keep class com.neoastra.backend.NeoAstraWebHost {
    public *;
    static native <methods>;
}
-keep class com.neoastra.backend.NeoAstraWebHost$Response {
    public <init>(...);
}

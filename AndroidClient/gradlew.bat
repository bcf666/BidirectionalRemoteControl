@rem Minimal gradlew for Windows
@echo off
setlocal
set DIR=%~dp0
set JAR=%DIR%gradle\wrapper\gradle-wrapper.jar
if not exist "%JAR%" (
    echo gradle-wrapper.jar not found at %JAR%
    exit /b 1
)
set JAVA_EXE=java
if defined JAVA_HOME (
    set JAVA_EXE=%JAVA_HOME%\bin\java.exe
)
"%JAVA_EXE%" -classpath "%JAR%" org.gradle.wrapper.GradleWrapperMain %*
endlocal

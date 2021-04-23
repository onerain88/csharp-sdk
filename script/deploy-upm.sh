#!/bin/sh
prefix=LeanCloud-SDK
version=0.0.12

storage=Storage
realtime=Realtime
livequery=LiveQuery

aot=AOT
unity=Unity

releasePath=bin/Release/netstandard2.0

storageAOTReleasePath=./$storage/$storage.$aot/$releasePath/
realtimeAOTReleasePath=./$livequery/$livequery.$aot/$releasePath/
unityReleasePath=./$storage/$storage.$unity/$releasePath

unityProjectPath=./Unity/UnityProject

deploy() {
  aotPath=$1
  service=$2

  pluginsPath=$unityProjectPath/Assets/Plugins
  mkdir $pluginsPath

  # 同步 AOT 库
  cp -r $aotPath/. $pluginsPath

  # 拷贝平台库
  cp $unityReleasePath/$storage.$unity.dll $pluginsPath/
  cp $unityReleasePath/$storage.$unity.pdb $pluginsPath/
  cp ./Unity/link.xml $pluginsPath/

  packagePath=$unityProjectPath/Assets/package.json
  # 拷贝 package.json 并替换版本号
  cat ./Unity/$service.package.json | sed 's/__VERSION__/'$version'/' > $packagePath

  # 使用 Unity Editor 打开工程，生成 .meta 文件
  /Applications/Unity/Unity.app/Contents/MacOS/Unity -batchmode -quit -nographics -silent-crashes -logFile log -projectPath=$unityProjectPath ; cat log

  # 创建发布目录
  upmPath=upm-$service
  mkdir $upmPath
  mv $pluginsPath $upmPath/
  mv $pluginsPath.meta $upmPath/
  mv $packagePath $upmPath/
  mv $packagePath.meta $upmPath/

  # TODO 推送 GitHub

}

deploy $storageAOTReleasePath storage
# deploy $realtimeAOTReleasePath realtime
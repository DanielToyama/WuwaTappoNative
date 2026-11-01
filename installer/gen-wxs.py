import os, uuid

import sys
version = sys.argv[1] if len(sys.argv) > 1 else '0.0.24'
root = 'publish/app-' + version
tree = {'files': [], 'dirs': {}}


def add(rel, src):
    parts = rel.split('\\')
    node = tree
    for p in parts[:-1]:
        node = node['dirs'].setdefault(p, {'files': [], 'dirs': {}})
    node['files'].append((parts[-1], src))


for dp, _, fs in os.walk(root):
    for f in fs:
        p = os.path.join(dp, f)
        rel = os.path.relpath(p, root).replace('/', '\\')
        add(rel, p)

components = []
dirseq = [0]


def uid(prefix, text, n=16):
    return prefix + uuid.uuid5(uuid.NAMESPACE_URL, text).hex[:n]


def emit(node, path, indent):
    pad = ' ' * indent
    out = []
    for name, child in sorted(node['dirs'].items()):
        full = (path + '\\' + name) if path else name
        did = uid('dir_', full, 12)
        out.append('%s<Directory Id="%s" Name="%s">' % (pad, did, name.replace('&', '&amp;')))
        out.extend(emit(child, full, indent + 2))
        out.append('%s</Directory>' % pad)
    for name, src in sorted(node['files']):
        cid = uid('cmp_', src)
        gid = uuid.uuid5(uuid.NAMESPACE_URL, 'component:' + src)
        fid = uid('fil_', 'file:' + src)
        components.append(cid)
        out.append('%s<Component Id="%s" Guid="{%s}"><File Id="%s" Source="%s" KeyPath="yes" /></Component>'
                   % (pad, cid, gid, fid, src.replace('&', '&amp;')))
    return out


body = emit(tree, '', 4)

lines = ['<?xml version="1.0" encoding="utf-8"?>',
         '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">',
         '  <Package Name="Wuwatappo Native" Manufacturer="Wuwatappo Native" Version="' + version + '" UpgradeCode="A7C3F6D2-5B1E-4E8A-9C2D-8F4B6A1D3E57" Scope="perUser" Language="2052" Codepage="936">',
         '    <MajorUpgrade DowngradeErrorMessage="已安装更新版本，请先卸载。" />',
         '    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />',
         '    <StandardDirectory Id="LocalAppDataFolder"><Directory Id="ProgramsFolder" Name="Programs"><Directory Id="INSTALLFOLDER" Name="Wuwatappo Native" /></Directory></StandardDirectory>',
         '    <StandardDirectory Id="ProgramMenuFolder"><Directory Id="AppMenuFolder" Name="Wuwatappo Native" /></StandardDirectory>',
         '    <StandardDirectory Id="DesktopFolder" />',
         '    <Feature Id="Main" Title="Wuwatappo Native" Level="1">']
lines += ['      <ComponentRef Id="%s" />' % c for c in components]
lines += ['      <ComponentRef Id="StartMenuShortcut" />', '      <ComponentRef Id="DesktopShortcut" />',
          '    </Feature>', '  </Package>',
          '  <Fragment><DirectoryRef Id="INSTALLFOLDER">']
lines += body
lines += ['  </DirectoryRef></Fragment>',
          '  <Fragment><DirectoryRef Id="AppMenuFolder"><Component Id="StartMenuShortcut" Guid="*"><Shortcut Id="StartMenuSC" Name="Wuwatappo Native" Target="[INSTALLFOLDER]WuwatappoNative.exe" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon" /><RemoveFolder Id="RemoveAppMenuFolder" On="uninstall" /><RegistryValue Root="HKCU" Key="Software\\WuwatappoNative" Name="StartMenu" Type="integer" Value="1" KeyPath="yes" /></Component></DirectoryRef></Fragment>',
          '  <Fragment><DirectoryRef Id="DesktopFolder"><Component Id="DesktopShortcut" Guid="*"><Shortcut Id="DesktopSC" Name="Wuwatappo Native" Target="[INSTALLFOLDER]WuwatappoNative.exe" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon" /><RegistryValue Root="HKCU" Key="Software\\WuwatappoNative" Name="Desktop" Type="integer" Value="1" KeyPath="yes" /></Component></DirectoryRef></Fragment>',
          '  <Fragment><Icon Id="AppIcon" SourceFile="assets/app.ico" /><Property Id="ARPPRODUCTICON" Value="AppIcon" /><Property Id="ARPNOMODIFY" Value="1" /></Fragment>',
          '</Wix>']

open('installer/product.wxs', 'w', encoding='utf-8').write('\n'.join(lines))
print('components', len(components))

// Host falso usado só quando a página é aberta fora do app (num navegador comum), para testar a interface.
'use strict';

window.MockHost = {
  create(deliver) {
    const GB = 2 ** 30, MB = 2 ** 20;
    const reply = (id, result, delay = 120) => setTimeout(() => deliver({ id, ok: true, result }), delay);
    const emit = (event, data) => deliver({ event, data });
    let scanTimer = null;

    const kid = (name, path, size, files, children = []) => ({
      name, path, size, files, dirs: Math.round(files / 12), modified: '2026-10-20T10:00:00', isLink: false,
      hasChildren: children.length > 0, preview: children.slice(0, 8).map(c => ({ name: c[0], size: c[1] })), _children: children,
    });
    const tree = {
      'C:\\': [
        kid('Users', 'C:\\Users', 123.4 * GB, 512000, [['usuario', 123.4 * GB]]),
        kid('Windows', 'C:\\Windows', 49.5 * GB, 210000, [['WinSxS', 20.2 * GB], ['System32', 12.2 * GB], ['Installer', 5.8 * GB]]),
        kid('Program Files', 'C:\\Program Files', 35.4 * GB, 98000, [['Docker', 4 * GB]]),
        kid('ProgramData', 'C:\\ProgramData', 35.1 * GB, 61000, [['McAfee', 16.5 * GB], ['Microsoft', 9.8 * GB]]),
        kid('Program Files (x86)', 'C:\\Program Files (x86)', 32.1 * GB, 77000, [['Microsoft', 12 * GB]]),
        kid('Projetos', 'C:\\Projetos', 20.7 * GB, 330000, [['trabalho', 20.5 * GB]]),
        kid('Drivers', 'C:\\Drivers', 3.8 * GB, 1200, [['Audio', 1.2 * GB]]),
        kid('Python314', 'C:\\Python314', 0.15 * GB, 4000),
        kid('tools', 'C:\\tools', 0.09 * GB, 77),
      ],
      'C:\\Users': [kid('usuario', 'C:\\Users\\usuario', 123.4 * GB, 512000, [['AppData', 113 * GB]])],
      'C:\\Users\\usuario': [
        kid('AppData', 'C:\\Users\\usuario\\AppData', 113.5 * GB, 400000, [['Local', 100 * GB], ['Roaming', 9 * GB]]),
        kid('.gradle', 'C:\\Users\\usuario\\.gradle', 3.9 * GB, 22000, [['caches', 3.5 * GB]]),
        kid('.nuget', 'C:\\Users\\usuario\\.nuget', 2.3 * GB, 40000, [['packages', 2.3 * GB]]),
        kid('.vscode', 'C:\\Users\\usuario\\.vscode', 2.25 * GB, 30000, [['extensions', 2.2 * GB]]),
        kid('Desktop', 'C:\\Users\\usuario\\Desktop', 0.25 * GB, 120),
      ],
    };
    const node = path => {
      const kids = tree[path] || [];
      const crumbs = [];
      let acc = '';
      path.split('\\').filter(Boolean).forEach((p, i) => { acc = i === 0 ? p + '\\' : acc.replace(/\\$/, '') + '\\' + p; crumbs.push({ name: i === 0 ? acc : p, path: acc }); });
      const size = kids.reduce((s, k) => s + k.size, 0) + (path === 'C:\\' ? 24.2 * GB : 0.03 * GB);
      return {
        name: crumbs.at(-1)?.name || path, path, size, filesSize: path === 'C:\\' ? 24.2 * GB : 0.03 * GB,
        files: kids.reduce((s, k) => s + k.files, 0), dirs: 9000,
        children: kids.map(({ _children, ...k }) => ({ ...k, hasChildren: !!tree[k.path] || k.hasChildren })),
        breadcrumb: crumbs,
      };
    };
    const summary = () => ({
      root: node('C:\\'), files: 1293450, bytes: 324 * GB, cloudBytes: 109.2 * GB, inaccessible: 12, seconds: 74,
      categories: [
        { name: 'Discos virtuais', bytes: 52 * GB }, { name: 'Bibliotecas e código', bytes: 61 * GB }, { name: 'Sistema e cache', bytes: 58 * GB },
        { name: 'Outros', bytes: 49 * GB }, { name: 'Instaladores', bytes: 31 * GB }, { name: 'Imagens', bytes: 9 * GB },
        { name: 'Compactados', bytes: 6 * GB }, { name: 'Vídeos', bytes: 3 * GB }, { name: 'Documentos', bytes: 1 * GB },
      ],
      extensions: [],
      topFiles: [
        ['C:\\Users\\usuario\\AppData\\Local\\Docker\\wsl\\disk\\docker_data.vhdx', 21.9 * GB, 'Discos virtuais'],
        ['C:\\hiberfil.sys', 12.7 * GB, 'Sistema e cache'], ['C:\\pagefile.sys', 11.5 * GB, 'Sistema e cache'],
        ['C:\\Users\\usuario\\AppData\\Local\\Android\\Sdk\\system-images\\android-34\\google_apis\\x86_64\\system.img', 4.1 * GB, 'Instaladores'],
        ['C:\\Users\\usuario\\AppData\\Local\\Packages\\CanonicalGroupLimited.Ubuntu20.04LTS_79rhkp1fndgsc\\LocalState\\ext4.vhdx', 3.8 * GB, 'Discos virtuais'],
        ['C:\\ProgramData\\McAfee\\wps\\log\\wps_2026.etl', 2.2 * GB, 'Sistema e cache'],
        ['C:\\Projetos\\backup-api.tar.gz', 0.9 * GB, 'Compactados'],
        ['C:\\Users\\usuario\\Videos\\gravacao-reuniao.mp4', 0.6 * GB, 'Vídeos'],
      ].map(([path, size, category]) => ({ path, size, category, name: path.split('\\').pop(), modified: '2026-09-12T08:00:00' })),
      heavy: [{ name: 'node_modules', bytes: 9.8 * GB, count: 14 }, { name: '.git', bytes: 2.1 * GB, count: 31 }, { name: 'bin', bytes: 1.4 * GB, count: 40 }, { name: 'obj', bytes: 0.9 * GB, count: 38 }],
    });

    const targets = [
      ['temp-user', 'Windows', 'Arquivos temporários do usuário', 'Pasta Temp do seu usuário. Só apaga arquivos com mais de 24 h.', 'seguro', false, false, { bytes: 8.4 * GB }],
      ['temp-windows', 'Windows', 'Arquivos temporários do Windows', 'C:\\Windows\\Temp. Só arquivos com mais de 24 h.', 'seguro', true, false, { bytes: 1.1 * GB }],
      ['recycle', 'Windows', 'Lixeira', 'Esvazia a Lixeira de todos os discos.', 'atencao', false, false, { bytes: 0, note: '2 item(ns)' }],
      ['hibernation', 'Windows', 'Desativar hibernação', 'Apaga o hiberfil.sys.', 'atencao', true, false, { bytes: 12.7 * GB }],
      ['shadow', 'Windows', 'Limitar pontos de restauração a 10 GB', 'Reduz o espaço das cópias de sombra.', 'atencao', true, false, { unknown: true, note: 'Reabra como administrador para medir.' }],
      ['dism', 'Windows', 'Limpeza de componentes do Windows (WinSxS)', 'Remove versões antigas de componentes (DISM).', 'seguro', true, false, { unknown: true, note: 'O ganho só aparece depois; costuma liberar de 1 a 5 GB.' }],
      ['mcafee-logs', 'Programas', 'Logs do McAfee', 'Registros de atividade do McAfee.', 'seguro', true, false, { bytes: 16.3 * GB }],
      ['dell-leftovers', 'Programas', 'Pacotes de drivers extraídos (Dell)', 'C:\\Drivers e C:\\dell.', 'atencao', true, true, { items: [{ path: 'C:\\Drivers', label: 'C:\\Drivers', size: 3.8 * GB, selected: true }, { path: 'C:\\dell', label: 'C:\\dell', size: 0.06 * GB, selected: true }] }],
      ['editor-cache', 'Programas', 'Cache do VS Code e do Cursor', 'Caches, logs e extensões baixadas dos editores.', 'seguro', false, true, { items: [{ path: 'a', label: 'Cursor · CachedExtensionVSIXs', size: 2.1 * GB, selected: true }, { path: 'b', label: 'Cursor · Cache', size: 0.8 * GB, selected: true }, { path: 'c', label: 'Code · CachedData', size: 0.4 * GB, selected: true }] }],
      ['docker-prune', 'Docker, WSL e VMs', 'Docker: cache de build e imagens sem uso', 'docker builder prune + docker image prune.', 'seguro', false, false, { bytes: 0, note: 'imagens: 0 B · cache de build: 0 B' }],
      ['vhdx-compact', 'Docker, WSL e VMs', 'Compactar discos virtuais (Docker/WSL)', 'Os arquivos .vhdx crescem e nunca encolhem sozinhos.', 'seguro', true, true, { unknown: true, note: '2 disco(s) somando 25,7 GB.', items: [{ path: 'd', label: 'Docker · imagens, containers e volumes', size: 21.9 * GB, selected: true }, { path: 'e', label: 'WSL · Ubuntu20.04LTS', size: 3.8 * GB, selected: true }] }],
      ['node-modules', 'Desenvolvimento', 'node_modules de projetos parados', 'Dependências de projetos sem alteração há mais de 90 dias.', 'seguro', false, true, { items: [
        { path: 'f', label: 'app-mobile', size: 2.9 * GB, lastActivity: '2026-10-27', selected: false },
        { path: 'g', label: 'loja-online\\frontend', size: 0.9 * GB, lastActivity: '2026-10-30', selected: false },
        { path: 'h', label: 'blog', size: 0.4 * GB, lastActivity: '2026-08-24', selected: false },
        { path: 'i', label: 'relatorios', size: 0.3 * GB, lastActivity: '2026-01-07', selected: true },
      ], note: '4 pasta(s) node_modules em C:\\Projetos · 1 parada(s) há mais de 90 dias.' }],
      ['gradle-cache', 'Desenvolvimento', 'Cache do Gradle (Android/Java)', 'Dependências e distribuições baixadas pelo Gradle.', 'seguro', false, true, { items: [{ path: 'j', label: 'Dependências (caches)', size: 3.5 * GB, selected: true }, { path: 'k', label: 'Distribuições do Gradle (wrapper)', size: 0.4 * GB, selected: true }] }],
      ['npm-cache', 'Desenvolvimento', 'Cache do npm', 'Pacotes baixados pelo npm.', 'seguro', false, false, { available: false, note: 'Cache vazio.' }],
    ];

    const apps = [
      ['Dell SupportAssist', 'Dell Inc.', 908 * MB, 'opcional', 'Diagnóstico e chamados de garantia da Dell.'],
      ['MyDell Components Installer', 'Dell, Technologies, Inc.', 379 * MB, 'remover', 'Portal MyDell e serviços de telemetria da Dell.'],
      ['McAfee', 'McAfee, LLC', 327 * MB, 'remover', 'Antivírus de teste que vem de fábrica. O Microsoft Defender assume.'],
      ['Docker Desktop', 'Docker Inc.', 3.9 * GB, '', null],
      ['Microsoft Visual Studio Code', 'Microsoft Corporation', 410 * MB, '', null],
      ['Dell Core Services', 'Dell, Inc.', 214 * MB, 'remover', 'Serviços de telemetria da Dell.'],
      ['Dell SupportAssist Remediation', 'Dell Inc.', 104 * MB, 'remover', 'Componente de recuperação do SupportAssist.'],
      ['WebAdvisor da McAfee', 'McAfee, LLC', 85 * MB, 'remover', 'Extensão do McAfee.'],
      ['Dell Pair', 'Dell Inc.', 78 * MB, 'remover', 'Só serve para parear periféricos Dell.'],
      ['Dell Optimizer', 'Dell', 70 * MB, 'remover', 'Ajustes automáticos de áudio e bateria.'],
      ['Realtek Audio Driver', 'Realtek Semiconductor Corp.', 60 * MB, 'manter', 'Driver. Recomendo manter.'],
    ].map(([name, publisher, size, verdict, advice], i) => ({ id: 'a' + i, name, publisher, size, verdict, advice, version: '1.0', installDate: '2025-03-10', source: 'win32' }));

    function handle({ id, method, params }) {
      switch (method) {
        case 'system.info': return reply(id, {
          platform: 'windows', isAdmin: false, canRelaunch: true, canPickFolder: true,
          systemDrive: 'C:\\', machine: 'NOTEBOOK', user: 'usuario',
          drives: [{ name: 'C:\\', label: 'Windows', format: 'NTFS', total: 476.9 * GB, free: 31.2 * GB }],
          hidden: [
            { icon: 'moon', title: 'Hibernação (hiberfil.sys)', sub: 'Pode ser apagado desativando a hibernação', bytes: 12.7 * GB },
            { icon: 'memory', title: 'Memória virtual (pagefile.sys)', sub: 'Gerenciado pelo Windows; não apague', bytes: 11.5 * GB },
            { icon: 'history', title: 'Pontos de restauração', sub: 'Reabra como administrador para medir', bytes: null },
            { icon: 'trash', title: 'Lixeira', sub: '2 item(ns)', bytes: 1.2 * MB },
          ],
          shadow: null, rootFilesHint: 'pagefile.sys, hiberfil.sys e swapfile.sys',
        });
        case 'scan.start': {
          reply(id, { path: params.path });
          let t = 0;
          scanTimer = setInterval(() => {
            t += 0.25;
            emit('scan.progress', { files: Math.round(t * 60000), bytes: t * 5 * GB, current: 'C:\\Users\\usuario\\AppData\\Local\\Packages\\Microsoft.Windows' + Math.round(t * 100), elapsed: t });
            if (t >= 2.5) { clearInterval(scanTimer); emit('scan.done', summary()); }
          }, 250);
          return;
        }
        case 'scan.cancel': clearInterval(scanTimer); emit('scan.cancelled', {}); return reply(id, true);
        case 'scan.node': return reply(id, node(params.path));
        case 'scan.summary': return reply(id, summary());
        case 'cleanup.list': return reply(id, {
          targets: targets.map(([id, category, title, description, risk, requiresAdmin, selectable]) => ({ id, category, title, description, risk, requiresAdmin, selectable })),
          projectRoots: ['C:\\Projetos'],
        });
        case 'cleanup.measure': {
          const t = targets.find(x => x[0] === params.id);
          const m = { available: true, bytes: 0, ...t[7] };
          if (m.items && !m.bytes) m.bytes = m.items.filter(i => i.selected).reduce((s, i) => s + i.size, 0);
          return reply(id, m, 300 + Math.random() * 1500);
        }
        case 'cleanup.run': {
          emit('log', { line: '▶ ' + params.id });
          [300, 700, 1100].forEach((d, i) => setTimeout(() => emit('log', { line: `  passo ${i + 1}: removendo arquivos…` }), d));
          setTimeout(() => emit('log', { line: '✔ Concluído. Espaço livre aumentou 3,2 GB.' }), 1400);
          return reply(id, { freed: 3.2 * GB, free: 34 * GB }, 1500);
        }
        case 'apps.list': return reply(id, apps, 900);
        case 'apps.uninstall': emit('log', { line: 'Abrindo o desinstalador…' }); return reply(id, { code: 0 }, 1200);
        case 'fs.delete': return reply(id, { ok: true, freed: 1.5 * GB });
        case 'dialog.pickFolder': return reply(id, 'C:\\Projetos');
        case 'account.info': return reply(id, { user: 'usuario', kind: 'local', canChange: true, reason: null, manageUrl: null, minLength: 8 });
        case 'account.changePassword': return setTimeout(() => deliver({ id, ok: false, error: 'Exemplo: a senha atual está incorreta.' }), 600);
        default: return reply(id, true);
      }
    }
    return { send: handle };
  },
};

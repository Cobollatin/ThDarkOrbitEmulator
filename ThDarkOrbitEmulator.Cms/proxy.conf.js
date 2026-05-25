const target = process.env.services__gateway__http__0 || 'http://localhost:60953';

module.exports = {
  "/api": {
    target,
    secure: false,
    changeOrigin: true,
    logLevel: "info"
  }
};
